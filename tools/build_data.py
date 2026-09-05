#!/usr/bin/env python3
"""Generate Gardener/Data/seeds.json and Gardener/Data/crossbreeds.json from data/vendor + data/sources.

seeds.json: one entry per outdoor `GardeningSeed` row (row > 0, not a flowerpot flower), carrying
grow hours (Lotlab), wilt hours (minimum of Lotlab and the community spreadsheet, disputed rows
flagged), crop/seed yield per soil tier, and gatherable/cross-only flags (nick75g).

crossbreeds.json: unordered {a,b,targets[]} pairs collapsed from nick75g's target-keyed
crossbreeding.json, plus a dead-pair list from the community spreadsheet's X / Dead cells.
data/overrides.json is applied last so a hand decision always wins over either source.

Every label is resolved to a row id through tools/_dataset.resolve_label; any label that does not
resolve is collected and reported, and the script exits non-zero rather than silently dropping it.

--check regenerates into a temp directory and diffs against the committed Gardener/Data/*.json,
exiting non-zero on drift (a source edited without regenerating the output).
"""
from __future__ import annotations

import argparse
import json
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _dataset import (  # noqa: E402
    REPO_ROOT,
    SOURCES_DIR,
    VENDOR_DIR,
    build_resolver,
    iter_matrix_pairs,
    load_aliases,
    load_overrides,
    load_vendor,
    matrix_axis,
    outdoor_rows,
    read_matrix_rows,
    resolve_label,
)

OUTPUT_DIR = REPO_ROOT / "Gardener" / "Data"


def parse_quad(cell: str) -> list[int] | None:
    """Parse "12 / 15 / 18 / 24"-shaped columns, tolerating inconsistent spacing. Empty -> None."""
    cell = cell.strip()
    if not cell:
        return None
    parts = [p.strip() for p in cell.split("/")]
    return [int(float(p)) for p in parts]


def load_yields_and_wilt(resolver: dict[str, int]) -> dict[int, dict]:
    rows = read_matrix_rows(SOURCES_DIR / "yields-and-wilt-times.csv")
    by_row: dict[int, dict] = {}
    for row in rows[1:]:
        if not row or not row[0].strip():
            continue
        label = row[0].strip()
        seed_row = resolve_label(label, resolver)
        if seed_row is None:
            # Rows for flowerpot-only produce (Arum, Tulip, ...) never resolve; that is expected,
            # not an error, since this table is restricted to the 81 outdoor rows below.
            continue
        crop_yield = parse_quad(row[1])
        seed_yield = parse_quad(row[2])
        wilt_raw = row[4].strip()
        wilt_hours = 0 if wilt_raw == "-" else int(float(wilt_raw))
        by_row[seed_row] = {
            "cropYield": crop_yield,
            "seedYield": seed_yield,
            "wiltHours": wilt_hours,
        }
    return by_row


def load_grow_and_wilt_lotlab() -> dict[int, dict]:
    rows = load_vendor("seeds_time.json")
    by_row = {}
    for entry in rows:
        # Row 107 carries a typo'd "GrowthTime" key instead of "GrowTime"; that is the single
        # documented data-quality gap in this table (docs/RESEARCH.md, "Is grow time fixed or a
        # range?"), and it falls on a flowerpot-only row, so it never reaches the outdoor set.
        by_row[entry["Index"]] = {
            "growHours": entry.get("GrowTime"),
            "wiltHours": entry.get("WiltTime"),
        }
    return by_row


def load_gather_flags(resolver: dict[str, int]) -> dict[int, dict]:
    """nick75g's seeds.json also flags the 27 flowerpot-only seeds; those never resolve against the
    outdoor-only resolver and that is expected, not an error — completeness is checked the other
    way around, in build_seeds, which requires every outdoor row to have a flag."""
    flags = load_vendor("seeds.json")
    by_row: dict[int, dict] = {}
    for label, flag in flags.items():
        seed_row = resolve_label(label, resolver)
        if seed_row is None:
            continue
        by_row[seed_row] = {
            "gatherable": flag in ("y", "yn"),
            "crossOnly": flag == "n",
        }
    return by_row


def build_seeds(resolver: dict[str, int], unresolved: list[str]) -> list[dict]:
    items = load_vendor("gardening_seed_items.json")
    lotlab = load_grow_and_wilt_lotlab()
    yields_wilt = load_yields_and_wilt(resolver)
    gather = load_gather_flags(resolver)

    seeds = []
    for r in sorted(outdoor_rows(items), key=lambda r: r["row"]):
        row = r["row"]
        lot = lotlab.get(row, {})
        yw = yields_wilt.get(row)
        gr = gather.get(row)

        if yw is None:
            unresolved.append(f"outdoor seed row {row} ({r['produceItemName']!r}) missing from yields-and-wilt-times.csv")
        if gr is None:
            unresolved.append(f"outdoor seed row {row} ({r['produceItemName']!r}) missing from nick75g seeds.json")

        grow_hours = lot.get("growHours")

        lotlab_wilt = lot.get("wiltHours")
        csv_wilt = yw["wiltHours"] if yw else None
        if lotlab_wilt is not None and csv_wilt is not None and lotlab_wilt != csv_wilt:
            wilt_hours = min(lotlab_wilt, csv_wilt)
            wilt_disputed = True
        else:
            wilt_hours = lotlab_wilt if lotlab_wilt is not None else csv_wilt
            wilt_disputed = False

        seeds.append(
            {
                "row": row,
                "growHours": grow_hours,
                "wiltHours": wilt_hours,
                "wiltDisputed": wilt_disputed,
                "cropYield": yw["cropYield"] if yw else None,
                "seedYield": yw["seedYield"] if yw else None,
                "gatherable": gr["gatherable"] if gr else None,
                "crossOnly": gr["crossOnly"] if gr else None,
            }
        )
    return seeds


def build_dead_pairs(resolver: dict[str, int], unresolved: list[str]) -> set[tuple[int, int]]:
    rows = read_matrix_rows(SOURCES_DIR / "trends-and-families.csv")
    axis = matrix_axis(rows)

    axis_rows: dict[int, int] = {}
    for grid_idx, label in axis:
        seed_row = resolve_label(label, resolver)
        if seed_row is None:
            unresolved.append(f"trends-and-families.csv axis label {label!r}")
            continue
        axis_rows[grid_idx] = seed_row

    dead: set[tuple[int, int]] = set()
    for ri, ci, result in iter_matrix_pairs(rows, axis):
        if ri not in axis_rows or ci not in axis_rows:
            continue
        if result.dead:
            a, b = sorted((axis_rows[ri], axis_rows[ci]))
            dead.add((a, b))
    return dead


def build_crossbreeds(resolver: dict[str, int], unresolved: list[str]) -> tuple[list[dict], set[tuple[int, int]]]:
    crossbreeding = load_vendor("crossbreeding.json")

    pair_targets: dict[tuple[int, int], set[int]] = {}
    for target_label, parent_pairs in crossbreeding.items():
        target_row = resolve_label(target_label, resolver)
        if target_row is None:
            unresolved.append(f"crossbreeding.json target label {target_label!r}")
            continue
        for parent_a, parent_b in parent_pairs:
            row_a = resolve_label(parent_a, resolver)
            row_b = resolve_label(parent_b, resolver)
            if row_a is None:
                unresolved.append(f"crossbreeding.json parent label {parent_a!r}")
                continue
            if row_b is None:
                unresolved.append(f"crossbreeding.json parent label {parent_b!r}")
                continue
            key = tuple(sorted((row_a, row_b)))
            pair_targets.setdefault(key, set()).add(target_row)

    dead = build_dead_pairs(resolver, unresolved)

    return pair_targets, dead


def apply_overrides(pair_targets: dict[tuple[int, int], set[int]], dead: set[tuple[int, int]]) -> None:
    overrides = load_overrides()
    for entry in overrides.get("pairs", []):
        a, b = entry["pair"]
        key = tuple(sorted((a, b)))
        chosen = entry["chosen"]
        if chosen.get("dead"):
            dead.add(key)
            pair_targets.pop(key, None)
        elif "targets" in chosen:
            pair_targets[key] = set(chosen["targets"])
            dead.discard(key)


def provenance() -> dict:
    prov_path = VENDOR_DIR / "provenance.json"
    vendor_prov = json.loads(prov_path.read_text()) if prov_path.exists() else {}
    return {
        "generated": datetime.now(timezone.utc).isoformat(),
        "lotlabCommit": vendor_prov.get("lotlabCommit"),
        "nick75gCommit": vendor_prov.get("nick75gCommit"),
        "xivapiSchema": vendor_prov.get("xivapiSchema"),
        "xivapiVersion": vendor_prov.get("xivapiVersion"),
    }


def generate(out_dir: Path) -> None:
    seed_items = load_vendor("gardening_seed_items.json")
    aliases = load_aliases()
    resolver = build_resolver(seed_items, aliases)

    unresolved: list[str] = []

    seeds = build_seeds(resolver, unresolved)
    pair_targets, dead = build_crossbreeds(resolver, unresolved)

    if unresolved:
        print("Unresolved labels (add to data/aliases.json or fix the source):", file=sys.stderr)
        for label in unresolved:
            print(f"  {label}", file=sys.stderr)
        sys.exit(1)

    apply_overrides(pair_targets, dead)

    prov = provenance()

    seeds_doc = {"provenance": prov, "seeds": seeds}
    pairs_doc = {
        "provenance": prov,
        "pairs": [
            {"a": a, "b": b, "targets": sorted(targets)}
            for (a, b), targets in sorted(pair_targets.items())
        ],
        "dead": [list(pair) for pair in sorted(dead)],
    }

    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "seeds.json").write_text(json.dumps(seeds_doc, indent=2) + "\n")
    (out_dir / "crossbreeds.json").write_text(json.dumps(pairs_doc, indent=2) + "\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="regenerate into a temp dir and diff against the committed output")
    args = parser.parse_args()

    if args.check:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            generate(tmp_dir)
            drift = False
            for name in ("seeds.json", "crossbreeds.json"):
                committed = OUTPUT_DIR / name
                generated = tmp_dir / name
                if not committed.exists():
                    print(f"DRIFT: {committed} does not exist; run tools/build_data.py", file=sys.stderr)
                    drift = True
                    continue
                # provenance.generated always differs; compare everything else.
                committed_doc = json.loads(committed.read_text())
                generated_doc = json.loads(generated.read_text())
                committed_doc["provenance"].pop("generated", None)
                generated_doc["provenance"].pop("generated", None)
                if committed_doc != generated_doc:
                    print(f"DRIFT: {name} does not match a fresh generation; run tools/build_data.py", file=sys.stderr)
                    drift = True
            if drift:
                return 1
            print("No drift: Gardener/Data/*.json matches a fresh generation.")
            return 0

    generate(OUTPUT_DIR)
    seeds_count = len(json.loads((OUTPUT_DIR / "seeds.json").read_text())["seeds"])
    cross_doc = json.loads((OUTPUT_DIR / "crossbreeds.json").read_text())
    target_rows = {t for pair in cross_doc["pairs"] for t in pair["targets"]}
    print(f"Wrote {OUTPUT_DIR / 'seeds.json'}: {seeds_count} seed entries.")
    print(
        f"Wrote {OUTPUT_DIR / 'crossbreeds.json'}: {len(cross_doc['pairs'])} pairs, "
        f"{len(target_rows)} distinct target rows, {len(cross_doc['dead'])} dead pairs."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
