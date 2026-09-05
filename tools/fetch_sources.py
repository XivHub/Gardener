#!/usr/bin/env python3
"""Refresh data/vendor/* from upstream and record a resolved provenance stamp per source.

Sources:
  nick75g/FFXIV-Crossbreed-Helper@main:_internal/importantfiles/  (MIT)      -> crossbreed working set
  Lotlab/FFXIV-Gardening-Tracker@master:GardeningTracker/data/seeds_time.json (GPL-3.0) -> grow/wilt hours
  XIVAPI v2 GardeningSeed sheet + Item(FilterGroup=20) search                -> seed/produce name -> row map
  ffxivgardening.com seed-details.php, one page per seed                    -> crossbreed efficiency + alternates

Every fetch is pinned to a resolved commit sha (GitHub) or schema+version string (XIVAPI), written to
data/vendor/provenance.json, so a later build can tell whether the bundled tables are stale.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _dataset import (  # noqa: E402
    FFXIVGARDENING_DIR,
    build_resolver,
    load_aliases,
    parse_ffxivgardening_crosses_table,
    parse_ffxivgardening_name,
    resolve_label,
)

REPO_ROOT = Path(__file__).resolve().parent.parent
VENDOR_DIR = REPO_ROOT / "data" / "vendor"

NICK75G_REPO = "nick75g/FFXIV-Crossbreed-Helper"
NICK75G_BRANCH = "main"
NICK75G_PATH = "_internal/importantfiles"
NICK75G_FILES = ["crossbreeding.json", "onlycross.json", "seeds.json", "gatherlist.json", "othersources.json"]

LOTLAB_REPO = "Lotlab/FFXIV-Gardening-Tracker"
LOTLAB_BRANCH = "master"
LOTLAB_PATH = "GardeningTracker/data"
LOTLAB_FILES = ["seeds_time.json"]

XIVAPI_BASE = "https://v2.xivapi.com/api"

FFXIVGARDENING_URL = "https://www.ffxivgardening.com/seed-details.php?SeedID={id}"
# Identifies the crawler and its purpose without carrying anyone's personal contact details.
FFXIVGARDENING_USER_AGENT = (
    "Gardener-Dalamud-Plugin-DataPipeline/1.0 (+https://plugins.xivhub.net; "
    "one-time data refresh for a FFXIV gardening plugin, polite crawl at 1 req/s)"
)
FFXIVGARDENING_FETCH_DELAY_SECONDS = 1.0
# How many consecutive non-resolving ids end the walk; tolerates one gap in an otherwise live range
# without letting an outage or a renumbering turn into an unbounded crawl.
FFXIVGARDENING_MAX_CONSECUTIVE_MISSES = 5


def http_get(url: str, headers: dict | None = None) -> bytes:
    req = urllib.request.Request(url, headers=headers or {"User-Agent": "Gardener-data-pipeline"})
    with urllib.request.urlopen(req, timeout=30) as resp:
        return resp.read()


def http_get_json(url: str) -> dict:
    return json.loads(http_get(url))


def resolve_commit_sha(repo: str, branch: str) -> str:
    data = http_get_json(f"https://api.github.com/repos/{repo}/commits/{branch}")
    return data["sha"]


def fetch_github_files(repo: str, sha: str, path: str, names: list[str]) -> None:
    for name in names:
        url = f"https://raw.githubusercontent.com/{repo}/{sha}/{path}/{name}"
        content = http_get(url)
        # Validate it is parseable JSON before writing; upstream files are all JSON.
        json.loads(content)
        (VENDOR_DIR / name).write_bytes(content)


def fetch_gardening_seed_items() -> dict:
    """Merge the GardeningSeed sheet and the FilterGroup==20 Item search into one row-indexed table.

    Each GardeningSeed row is bijective with exactly one Item whose FilterGroup is 20; that item is
    the seed, GardeningSeed.Item is the produce. Both endpoints are pinned to the same exdschema
    revision, checked below, so the merge is safe.
    """
    seed_sheet = http_get_json(f"{XIVAPI_BASE}/sheet/GardeningSeed?fields=Item.Name,IsPlantPotFlowerSeed&limit=200")
    seed_items = http_get_json(
        f"{XIVAPI_BASE}/search?sheets=Item&query=FilterGroup=20&fields=Name,AdditionalData.row_id&limit=250"
    )

    if seed_sheet["schema"] != seed_items["schema"] or seed_sheet["version"] != seed_items["version"]:
        print(
            "ERROR: GardeningSeed sheet and Item search returned different exdschema stamps; "
            "the merge below would not be self-consistent.",
            file=sys.stderr,
        )
        sys.exit(1)

    produce_by_row: dict[int, dict] = {}
    for row in seed_sheet["rows"]:
        row_id = row["row_id"]
        if row_id == 0:
            continue
        produce_by_row[row_id] = {
            "produceItemId": row["fields"]["Item"]["value"],
            "produceItemName": row["fields"]["Item"]["fields"]["Name"],
            "isPlantPotFlowerSeed": row["fields"]["IsPlantPotFlowerSeed"],
        }

    entries = []
    seed_rows_seen = set()
    for result in seed_items["results"]:
        row_id = result["fields"]["AdditionalData"]["row_id"]
        produce = produce_by_row.get(row_id)
        if produce is None:
            print(f"ERROR: seed item {result['row_id']} points at GardeningSeed row {row_id}, which is not row 0"
                  " and not returned by the sheet fetch.", file=sys.stderr)
            sys.exit(1)
        seed_rows_seen.add(row_id)
        entries.append(
            {
                "row": row_id,
                "seedItemId": result["row_id"],
                "seedItemName": result["fields"]["Name"],
                "produceItemId": produce["produceItemId"],
                "produceItemName": produce["produceItemName"],
                "isPlantPotFlowerSeed": produce["isPlantPotFlowerSeed"],
            }
        )

    missing = set(produce_by_row) - seed_rows_seen
    if missing:
        print(f"ERROR: GardeningSeed rows with no FilterGroup==20 seed item: {sorted(missing)}", file=sys.stderr)
        sys.exit(1)

    entries.sort(key=lambda e: e["row"])
    return {
        "schema": seed_sheet["schema"],
        "version": seed_sheet["version"],
        "rows": entries,
    }


def fetch_ffxivgardening_pages(refresh: bool) -> list[int]:
    """Walk SeedID=1.. and cache every page that resolves to a seed name.

    A resolving page is written to data/vendor/ffxivgardening/seed-<id>.html and, absent
    --refresh, loaded from there on a later run instead of hitting the network. An id that does
    not resolve is never cached, so the tail past the last known id is re-probed (bounded by
    FFXIVGARDENING_MAX_CONSECUTIVE_MISSES) every run, which is how a seed the site adds later gets
    picked up without a full --refresh.
    """
    FFXIVGARDENING_DIR.mkdir(parents=True, exist_ok=True)
    found: list[int] = []
    consecutive_misses = 0
    seed_id = 1
    while consecutive_misses < FFXIVGARDENING_MAX_CONSECUTIVE_MISSES:
        cache_path = FFXIVGARDENING_DIR / f"seed-{seed_id}.html"
        if cache_path.exists() and not refresh:
            html = cache_path.read_text(encoding="utf-8")
        else:
            content = http_get(
                FFXIVGARDENING_URL.format(id=seed_id),
                headers={"User-Agent": FFXIVGARDENING_USER_AGENT},
            )
            html = content.decode("utf-8")
            time.sleep(FFXIVGARDENING_FETCH_DELAY_SECONDS)

        if parse_ffxivgardening_name(html) is not None:
            cache_path.write_text(html, encoding="utf-8")
            found.append(seed_id)
            consecutive_misses = 0
        else:
            consecutive_misses += 1
        seed_id += 1

    return found


def build_ffxivgardening_crosses(page_ids: list[int], resolver: dict[str, int]) -> tuple[list[dict], list[str]]:
    """Every confirmed-crossbreed row across every cached page, with every name resolved to a
    `GardeningSeed` row id.

    A page with zero rows (every flowerpot flower: flowerpots cannot crossbreed) is skipped before
    its own name is even resolved, since the outdoor-only resolver would otherwise reject it for a
    reason that has nothing to do with a data problem.
    """
    crosses: list[dict] = []
    unresolved: list[str] = []
    for seed_id in page_ids:
        html = (FFXIVGARDENING_DIR / f"seed-{seed_id}.html").read_text(encoding="utf-8")
        rows = parse_ffxivgardening_crosses_table(html)
        if not rows:
            continue

        target_name = parse_ffxivgardening_name(html)
        target_row = resolve_label(target_name, resolver)
        if target_row is None:
            unresolved.append(f"seed-{seed_id}.html target {target_name!r}")
            continue

        for row in rows:
            parent_a_row = resolve_label(row["parentA"], resolver)
            parent_b_row = resolve_label(row["parentB"], resolver)
            alt_row = resolve_label(row["alternate"], resolver) if row["alternate"] else None
            if parent_a_row is None:
                unresolved.append(f"seed-{seed_id}.html parent {row['parentA']!r}")
            if parent_b_row is None:
                unresolved.append(f"seed-{seed_id}.html parent {row['parentB']!r}")
            if row["alternate"] and alt_row is None:
                unresolved.append(f"seed-{seed_id}.html alternate {row['alternate']!r}")
            if parent_a_row is None or parent_b_row is None or (row["alternate"] and alt_row is None):
                continue

            crosses.append(
                {
                    "parentA": parent_a_row,
                    "parentB": parent_b_row,
                    "target": target_row,
                    "alternate": alt_row,
                    "efficiency": row["efficiency"],
                }
            )

    return crosses, unresolved


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--refresh",
        action="store_true",
        help="ignore data/vendor/ffxivgardening/*.html cache and refetch every seed page",
    )
    args = parser.parse_args()

    VENDOR_DIR.mkdir(parents=True, exist_ok=True)

    provenance_path = VENDOR_DIR / "provenance.json"
    previous = json.loads(provenance_path.read_text()) if provenance_path.exists() else None

    print("Resolving nick75g/FFXIV-Crossbreed-Helper@main ...")
    nick75g_sha = resolve_commit_sha(NICK75G_REPO, NICK75G_BRANCH)
    fetch_github_files(NICK75G_REPO, nick75g_sha, NICK75G_PATH, NICK75G_FILES)
    print(f"  commit {nick75g_sha}")

    print("Resolving Lotlab/FFXIV-Gardening-Tracker@master ...")
    lotlab_sha = resolve_commit_sha(LOTLAB_REPO, LOTLAB_BRANCH)
    fetch_github_files(LOTLAB_REPO, lotlab_sha, LOTLAB_PATH, LOTLAB_FILES)
    print(f"  commit {lotlab_sha}")

    print("Fetching GardeningSeed + Item(FilterGroup=20) from XIVAPI v2 ...")
    seed_items = fetch_gardening_seed_items()
    (VENDOR_DIR / "gardening_seed_items.json").write_text(json.dumps(seed_items, indent=2) + "\n")
    print(f"  schema {seed_items['schema']} version {seed_items['version']}, {len(seed_items['rows'])} rows")

    print("Fetching www.ffxivgardening.com seed-details.php pages ...")
    page_ids = fetch_ffxivgardening_pages(refresh=args.refresh)
    print(f"  found {len(page_ids)} pages, ids {min(page_ids)}-{max(page_ids)}")
    if len(page_ids) != max(page_ids) - min(page_ids) + 1:
        print("  WARNING: the id range is not contiguous; at least one SeedID in range did not resolve.")

    print("Parsing www.ffxivgardening.com crossbreed tables ...")
    resolver = build_resolver(seed_items, load_aliases())
    crosses, unresolved_ffxivgardening = build_ffxivgardening_crosses(page_ids, resolver)
    if unresolved_ffxivgardening:
        print("ERROR: unresolved ffxivgardening.com seed names (add to data/aliases.json):", file=sys.stderr)
        for name in sorted(set(unresolved_ffxivgardening)):
            print(f"  {name}", file=sys.stderr)
        sys.exit(1)
    ffxivgardening_fetched = datetime.now(timezone.utc).isoformat()
    crosses_doc = {
        "generated": ffxivgardening_fetched,
        "pageCount": len(page_ids),
        "crosses": crosses,
    }
    (VENDOR_DIR / "ffxivgardening-crosses.json").write_text(json.dumps(crosses_doc, indent=2) + "\n")
    print(f"  wrote {len(crosses)} confirmed-cross rows across {len(page_ids)} pages")

    record = {
        "generated": datetime.now(timezone.utc).isoformat(),
        "nick75gCommit": nick75g_sha,
        "lotlabCommit": lotlab_sha,
        "xivapiSchema": seed_items["schema"],
        "xivapiVersion": seed_items["version"],
        "ffxivgardeningFetched": ffxivgardening_fetched,
        "ffxivgardeningPageCount": len(page_ids),
    }

    print("\nResolved provenance:")
    print(json.dumps(record, indent=2))
    if previous is not None:
        changed = {k: (previous.get(k), v) for k, v in record.items() if k != "generated" and previous.get(k) != v}
        if changed:
            print("\nStamps changed since last fetch:")
            for k, (old, new) in changed.items():
                print(f"  {k}: {old} -> {new}")
        else:
            print("\nNo stamp changed since last fetch (only 'generated' differs).")

    provenance_path.write_text(json.dumps(record, indent=2) + "\n")
    print(f"\nWrote {provenance_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
