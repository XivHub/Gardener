#!/usr/bin/env python3
"""Refresh data/vendor/* from upstream and record a resolved provenance stamp per source.

Sources:
  nick75g/FFXIV-Crossbreed-Helper@main:_internal/importantfiles/  (MIT)      -> crossbreed working set
  Lotlab/FFXIV-Gardening-Tracker@master:GardeningTracker/data/seeds_time.json (GPL-3.0) -> grow/wilt hours
  XIVAPI v2 GardeningSeed sheet + Item(FilterGroup=20) search                -> seed/produce name -> row map

Every fetch is pinned to a resolved commit sha (GitHub) or schema+version string (XIVAPI), written to
data/vendor/provenance.json, so a later build can tell whether the bundled tables are stale.
"""
from __future__ import annotations

import json
import sys
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

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


def main() -> int:
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

    record = {
        "generated": datetime.now(timezone.utc).isoformat(),
        "nick75gCommit": nick75g_sha,
        "lotlabCommit": lotlab_sha,
        "xivapiSchema": seed_items["schema"],
        "xivapiVersion": seed_items["version"],
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
