#!/usr/bin/env python3
"""Emit docs/cross-diff.md: nick75g's crossbreeding.json against two independent sources.

nick75g is the working dataset (tools/build_data.py emits crossbreeds.json from it); the
community spreadsheet and ffxivgardening.com are validation only, and neither ever overwrites it
silently. The spreadsheet check parses trends-and-families.csv's live cells into the same
{a,b}->targets shape, resolving every offspring token through the same resolver as build_data.py
plus data/aliases.json's matrixTokens:

  - unresolved offspring tokens (exits non-zero while any remain)
  - matrix-only pairs (matrix has a live cross the nick75g dataset does not)
  - nick75g-only pairs (nick75g has a cross the matrix has no live cell for)
  - shared pairs whose offspring sets differ
  - dead/live contradictions (matrix marks a pair dead that nick75g lists as producing something)

A difference with a matching entry in data/overrides.json is reported as decided, with the chosen
value and reason inline; every other difference is undecided, and this script's own exit status
reflects unresolved tokens only (overrides are a human review step, not a parse error).

The ffxivgardening.com check compares the same {a,b}->targets shape, built from a pair's produced
seed plus its recorded alternate outcome (data/vendor/ffxivgardening-crosses.json), against the
final, override-applied Gardener/Data/crossbreeds.json — the shipped dataset, not the raw nick75g
input, since "the targets already recorded" is what the plugin actually bundles. It also compares
each seed's grow hours, wilt hours, crop/seed yield and producing-pair count against the same
page's own info block. Every seed name on ffxivgardening.com is pre-resolved to a `GardeningSeed`
row id at fetch time (tools/fetch_sources.py fails loudly there), so nothing here can silently
drop a row; a name that only matches a flowerpot-only row is skipped as out of Gardener's scope,
same as every other source in this pipeline.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _dataset import (  # noqa: E402
    FFXIVGARDENING_DIR,
    REPO_ROOT,
    SOURCES_DIR,
    build_resolver,
    iter_matrix_pairs,
    load_aliases,
    load_overrides,
    load_vendor,
    matrix_axis,
    outdoor_rows,
    parse_ffxivgardening_crosses_table,
    parse_ffxivgardening_header,
    parse_ffxivgardening_name,
    read_matrix_rows,
    resolve_label,
)

CROSS_DIFF_PATH = REPO_ROOT / "docs" / "cross-diff.md"
CROSSBREEDS_PATH = REPO_ROOT / "Gardener" / "Data" / "crossbreeds.json"
SEEDS_PATH = REPO_ROOT / "Gardener" / "Data" / "seeds.json"


def build_matrix_token_resolver(resolver: dict[str, int], aliases: dict) -> dict[str, int]:
    combined = dict(resolver)
    for token, entry in aliases.get("matrixTokens", {}).items():
        combined[token] = entry["row"]
    return combined


def build_nick_pairs(resolver: dict[str, int]) -> dict[tuple[int, int], set[int]]:
    crossbreeding = load_vendor("crossbreeding.json")
    pair_targets: dict[tuple[int, int], set[int]] = {}
    for target_label, parent_pairs in crossbreeding.items():
        target_row = resolve_label(target_label, resolver)
        for parent_a, parent_b in parent_pairs:
            row_a = resolve_label(parent_a, resolver)
            row_b = resolve_label(parent_b, resolver)
            key = tuple(sorted((row_a, row_b)))
            pair_targets.setdefault(key, set()).add(target_row)
    return pair_targets


def build_matrix_relations(matrix_resolver: dict[str, int]) -> tuple[
    dict[tuple[int, int], set[int]], set[tuple[int, int]], set[str]
]:
    rows = read_matrix_rows(SOURCES_DIR / "trends-and-families.csv")
    axis = matrix_axis(rows)
    axis_rows = {i: resolve_label(label, matrix_resolver) for i, label in axis}

    pair_targets: dict[tuple[int, int], set[int]] = {}
    dead: set[tuple[int, int]] = set()
    unresolved_tokens: set[str] = set()

    for ri, ci, result in iter_matrix_pairs(rows, axis):
        a, b = axis_rows[ri], axis_rows[ci]
        key = tuple(sorted((a, b)))
        if result.dead:
            dead.add(key)
            continue
        for token in result.tokens:
            row = resolve_label(token, matrix_resolver)
            if row is None:
                unresolved_tokens.add(token)
            else:
                pair_targets.setdefault(key, set()).add(row)

    return pair_targets, dead, unresolved_tokens


def format_pair(pair: tuple[int, int]) -> str:
    return f"{pair[0]}+{pair[1]}"


def override_for(overrides: dict, pair: tuple[int, int]) -> dict | None:
    for entry in overrides.get("pairs", []):
        if tuple(sorted(entry["pair"])) == pair:
            return entry
    return None


def build_ffxivgardening_pair_targets() -> dict[tuple[int, int], set[int]]:
    """{a,b}->targets from ffxivgardening.com: a pair's produced seed plus its recorded alternate.

    Every id in data/vendor/ffxivgardening-crosses.json is already a resolved `GardeningSeed` row
    (tools/fetch_sources.py fails loudly on a name it cannot resolve), so no resolver is needed
    here.
    """
    crosses = load_vendor("ffxivgardening-crosses.json")["crosses"]
    pair_targets: dict[tuple[int, int], set[int]] = {}
    for row in crosses:
        key = tuple(sorted((row["parentA"], row["parentB"])))
        targets = pair_targets.setdefault(key, set())
        targets.add(row["target"])
        if row["alternate"] is not None:
            targets.add(row["alternate"])
    return pair_targets


def load_final_crossbreeds() -> tuple[dict[tuple[int, int], set[int]], set[tuple[int, int]]]:
    """The shipped Gardener/Data/crossbreeds.json, override-applied — the "targets already
    recorded" that ffxivgardening.com's produced-seed-plus-alternate column is checked against."""
    doc = json.loads(CROSSBREEDS_PATH.read_text())
    pair_targets = {(p["a"], p["b"]): set(p["targets"]) for p in doc["pairs"]}
    dead = {tuple(sorted(pair)) for pair in doc["dead"]}
    return pair_targets, dead


def build_ffxivgardening_seed_stats(resolver: dict[str, int]) -> dict[int, dict]:
    """Grow hours, wilt hours, crop/seed yield and producing-pair count per outdoor seed, read
    straight from its cached ffxivgardening.com page.

    A page whose name only matches a flowerpot-only row (every flowerpot flower page: flowerpots
    cannot crossbreed) is skipped, the same way every other source in this pipeline treats a label
    outside Gardener's outdoor-only scope.
    """
    stats: dict[int, dict] = {}
    for path in sorted(FFXIVGARDENING_DIR.glob("seed-*.html")):
        html = path.read_text(encoding="utf-8")
        name = parse_ffxivgardening_name(html)
        row = resolve_label(name, resolver)
        if row is None:
            continue
        header = parse_ffxivgardening_header(html)
        header["producingPairCount"] = len(parse_ffxivgardening_crosses_table(html))
        stats[row] = header
    return stats


def main() -> int:
    seed_items = load_vendor("gardening_seed_items.json")
    aliases = load_aliases()
    resolver = build_resolver(seed_items, aliases)
    matrix_resolver = build_matrix_token_resolver(resolver, aliases)
    overrides = load_overrides()

    nick_pairs = build_nick_pairs(resolver)
    matrix_pairs, matrix_dead, unresolved_tokens = build_matrix_relations(matrix_resolver)

    matrix_only = sorted(set(matrix_pairs) - set(nick_pairs))
    nick_only = sorted(set(nick_pairs) - set(matrix_pairs))
    shared = set(matrix_pairs) & set(nick_pairs)
    different = sorted(k for k in shared if matrix_pairs[k] != nick_pairs[k])
    contradictions = sorted(matrix_dead & set(nick_pairs))

    lines = ["# Cross matrix vs nick75g diff", ""]
    lines.append(
        "Community spreadsheet (`data/sources/trends-and-families.csv`, validation only) compared "
        "against nick75g's `crossbreeding.json` (the working dataset `crossbreeds.json` is built "
        "from), and both compared against a third independent source, ffxivgardening.com. Every "
        "difference below either carries a decision from `data/overrides.json` or is undecided; "
        "none of these sources ever overwrites the shipped dataset silently."
    )
    lines.append("")

    lines.append(f"## Unresolved offspring tokens ({len(unresolved_tokens)})")
    lines.append("")
    if unresolved_tokens:
        lines.append("Add these to `data/aliases.json`'s `matrixTokens` section.")
        lines.append("")
        for token in sorted(unresolved_tokens):
            lines.append(f"- `{token}`")
    else:
        lines.append("None.")
    lines.append("")

    def render_pair_section(title: str, pairs: list[tuple[int, int]], detail) -> None:
        lines.append(f"## {title} ({len(pairs)})")
        lines.append("")
        if not pairs:
            lines.append("None.")
            lines.append("")
            return
        undecided = 0
        for pair in pairs:
            override = override_for(overrides, pair)
            text = detail(pair)
            if override is not None:
                lines.append(f"- {format_pair(pair)}: {text} — **decided**: {override['chosen']} ({override['why']})")
            else:
                lines.append(f"- {format_pair(pair)}: {text} — **undecided**")
                undecided += 1
        lines.append("")
        if undecided:
            lines.append(f"{undecided} undecided in this section.")
            lines.append("")

    render_pair_section(
        "Matrix-only pairs", matrix_only,
        lambda p: f"matrix targets {sorted(matrix_pairs[p])}, absent from nick75g",
    )
    render_pair_section(
        "nick75g-only pairs", nick_only,
        lambda p: f"nick75g targets {sorted(nick_pairs[p])}, no live matrix cell",
    )
    render_pair_section(
        "Shared pairs with different offspring", different,
        lambda p: f"matrix {sorted(matrix_pairs[p])} vs nick75g {sorted(nick_pairs[p])}",
    )
    render_pair_section(
        "Dead/live contradictions", contradictions,
        lambda p: f"matrix marks dead, nick75g targets {sorted(nick_pairs[p])}",
    )

    # --- ffxivgardening.com: pair/target reconciliation against the shipped dataset ---
    # Compared against Gardener/Data/crossbreeds.json (override-applied) rather than raw
    # nick75g/matrix input, since that is "the targets already recorded" the plugin ships.
    ffx_pairs = build_ffxivgardening_pair_targets()
    final_pairs, final_dead = load_final_crossbreeds()

    ffx_absent_from_final = set(ffx_pairs) - set(final_pairs)
    ffx_only = sorted(p for p in ffx_absent_from_final if p not in final_dead)
    ffx_dead_contradictions = sorted(p for p in ffx_absent_from_final if p in final_dead)
    final_only = sorted(set(final_pairs) - set(ffx_pairs))
    ffx_shared = set(ffx_pairs) & set(final_pairs)
    ffx_different = sorted(k for k in ffx_shared if ffx_pairs[k] != final_pairs[k])
    ffx_agreeing = len(ffx_shared) - len(ffx_different)

    lines.append("## ffxivgardening.com vs the shipped dataset")
    lines.append("")
    lines.append(
        f"{len(ffx_pairs)} pairs have at least one confirmed crossbreed row on ffxivgardening.com "
        f"(produced seed plus its recorded alternate outcome, where the site has one). "
        f"{len(ffx_shared)} of those pairs also appear in the shipped `crossbreeds.json`, and "
        f"{ffx_agreeing} of those {len(ffx_shared)} agree on the full target set."
    )
    lines.append("")

    render_pair_section(
        "ffxivgardening.com-only pairs", ffx_only,
        lambda p: f"ffxivgardening.com targets {sorted(ffx_pairs[p])}, absent from crossbreeds.json",
    )
    render_pair_section(
        "Gardener-only pairs (no ffxivgardening.com confirmation)", final_only,
        lambda p: f"crossbreeds.json targets {sorted(final_pairs[p])}, no confirmed ffxivgardening.com row",
    )
    render_pair_section(
        "Shared pairs with different offspring (ffxivgardening.com)", ffx_different,
        lambda p: f"ffxivgardening.com {sorted(ffx_pairs[p])} vs crossbreeds.json {sorted(final_pairs[p])}",
    )
    render_pair_section(
        "Dead/live contradictions (ffxivgardening.com)", ffx_dead_contradictions,
        lambda p: f"crossbreeds.json marks dead, ffxivgardening.com targets {sorted(ffx_pairs[p])}",
    )

    # --- ffxivgardening.com: per-seed grow/wilt/yield/producing-pair-count cross-check ---
    ffx_stats = build_ffxivgardening_seed_stats(resolver)
    bundled_seeds = {s["row"]: s for s in json.loads(SEEDS_PATH.read_text())["seeds"]}

    # Producing-pair count per row from the shipped dataset, compared against the count of
    # confirmed-cross rows on that seed's own ffxivgardening.com page.
    bundled_producing_pairs: dict[int, int] = {}
    for targets in final_pairs.values():
        for target in targets:
            bundled_producing_pairs[target] = bundled_producing_pairs.get(target, 0) + 1

    seed_field_mismatches: list[tuple[int, str, object, object]] = []
    outdoor_no_ffx_page: list[int] = []
    for r in sorted(outdoor_rows(seed_items), key=lambda r: r["row"]):
        row = r["row"]
        ffx = ffx_stats.get(row)
        if ffx is None:
            outdoor_no_ffx_page.append(row)
            continue
        bundled = bundled_seeds.get(row)
        if bundled is None:
            continue
        for field in ("growHours", "wiltHours", "cropYield", "seedYield"):
            if ffx[field] != bundled[field]:
                seed_field_mismatches.append((row, field, bundled[field], ffx[field]))
        bundled_count = bundled_producing_pairs.get(row, 0)
        if ffx["producingPairCount"] != bundled_count:
            seed_field_mismatches.append((row, "producingPairCount", bundled_count, ffx["producingPairCount"]))

    lines.append(f"## ffxivgardening.com per-seed field mismatches ({len(seed_field_mismatches)})")
    lines.append("")
    lines.append(
        "Grow hours, wilt hours, crop/seed yield and producing-pair count, each read from a "
        "seed's own ffxivgardening.com page and compared against the bundled value."
    )
    lines.append("")
    if seed_field_mismatches:
        for row, field, bundled_value, ffx_value in seed_field_mismatches:
            lines.append(f"- row {row}, `{field}`: bundled {bundled_value!r} vs ffxivgardening.com {ffx_value!r}")
    else:
        lines.append("None.")
    lines.append("")

    # --- per-seed yield substitutions: data/overrides.json's seedYields, chosen in favour of
    # ffxivgardening.com over yields-and-wilt-times.csv where the two disagreed ---
    row_names = {r["row"]: r["produceItemName"] for r in seed_items["rows"]}
    yield_overrides = sorted(overrides.get("seedYields", []), key=lambda e: (e["row"], e["field"]))
    lines.append(f"## Per-seed yield substitutions ({len(yield_overrides)})")
    lines.append("")
    lines.append(
        "Seeds where `data/sources/yields-and-wilt-times.csv` and a seed's own "
        "ffxivgardening.com page disagreed on cropYield or seedYield; `data/overrides.json`'s "
        "`seedYields` entries record the site's value as chosen, and `Gardener/Data/seeds.json` "
        "carries that value rather than the spreadsheet's."
    )
    lines.append("")
    if yield_overrides:
        for entry in yield_overrides:
            name = row_names.get(entry["row"], f"row {entry['row']}")
            lines.append(
                f"- {name} (row {entry['row']}) `{entry['field']}`: spreadsheet "
                f"{entry['spreadsheetValue']!r} — **decided**: site's {entry['chosen']['value']!r} "
                f"({entry['why']})"
            )
    else:
        lines.append("None.")
    lines.append("")

    lines.append(f"## Outdoor seeds with no ffxivgardening.com page ({len(outdoor_no_ffx_page)})")
    lines.append("")
    if outdoor_no_ffx_page:
        for row in outdoor_no_ffx_page:
            lines.append(f"- row {row}")
    else:
        lines.append("None.")
    lines.append("")

    total_undecided = sum(
        1
        for pairs in (matrix_only, nick_only, different, contradictions, ffx_only, final_only, ffx_different, ffx_dead_contradictions)
        for pair in pairs
        if override_for(overrides, pair) is None
    )

    CROSS_DIFF_PATH.write_text("\n".join(lines) + "\n")
    print(f"Wrote {CROSS_DIFF_PATH}")
    print(
        f"Matrix vs nick75g — unresolved tokens: {len(unresolved_tokens)}; matrix-only: {len(matrix_only)}; "
        f"nick-only: {len(nick_only)}; different: {len(different)}; "
        f"dead/live contradictions: {len(contradictions)}"
    )
    print(
        f"ffxivgardening.com vs shipped dataset — pairs compared: {len(ffx_shared)}, agree: {ffx_agreeing}; "
        f"ffx-only: {len(ffx_only)}; Gardener-only: {len(final_only)}; different: {len(ffx_different)}; "
        f"dead/live contradictions: {len(ffx_dead_contradictions)}"
    )
    print(
        f"ffxivgardening.com per-seed — field mismatches: {len(seed_field_mismatches)}; "
        f"outdoor seeds with no page: {len(outdoor_no_ffx_page)}"
    )
    print(f"Undecided pair-level entries across all sources: {total_undecided}")

    if unresolved_tokens:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
