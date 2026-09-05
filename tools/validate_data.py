#!/usr/bin/env python3
"""Emit docs/cross-diff.md: the community spreadsheet vs nick75g's crossbreeding.json.

nick75g is the working dataset (tools/build_data.py emits crossbreeds.json from it);
trends-and-families.csv is validation only. This script parses the matrix's live cells into the
same {a,b}->targets shape, resolving every offspring token through the same resolver as
build_data.py plus data/aliases.json's matrixTokens, and reports:

  - unresolved offspring tokens (exits non-zero while any remain)
  - matrix-only pairs (matrix has a live cross the nick75g dataset does not)
  - nick75g-only pairs (nick75g has a cross the matrix has no live cell for)
  - shared pairs whose offspring sets differ
  - dead/live contradictions (matrix marks a pair dead that nick75g lists as producing something)

A difference with a matching entry in data/overrides.json is reported as decided, with the chosen
value and reason inline; every other difference is undecided, and this script's own exit status
reflects unresolved tokens only (overrides are a human review step, not a parse error).
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _dataset import (  # noqa: E402
    REPO_ROOT,
    SOURCES_DIR,
    build_resolver,
    iter_matrix_pairs,
    load_aliases,
    load_overrides,
    load_vendor,
    matrix_axis,
    read_matrix_rows,
    resolve_label,
)

CROSS_DIFF_PATH = REPO_ROOT / "docs" / "cross-diff.md"


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
        "from). Every difference below either carries a decision from `data/overrides.json` or is "
        "undecided."
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

    total_undecided = sum(
        1
        for pairs in (matrix_only, nick_only, different, contradictions)
        for pair in pairs
        if override_for(overrides, pair) is None
    )

    CROSS_DIFF_PATH.write_text("\n".join(lines) + "\n")
    print(f"Wrote {CROSS_DIFF_PATH}")
    print(
        f"Unresolved tokens: {len(unresolved_tokens)}; matrix-only: {len(matrix_only)}; "
        f"nick-only: {len(nick_only)}; different: {len(different)}; "
        f"dead/live contradictions: {len(contradictions)}; undecided pair-level entries: {total_undecided}"
    )

    if unresolved_tokens:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
