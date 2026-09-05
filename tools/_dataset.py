"""Shared label resolution and matrix parsing for build_data.py and validate_data.py.

Every seed label in every source (nick75g's item names, the community spreadsheet's produce
names) is resolved to a `GardeningSeed` row id through one rule set: the seed item name, the
produce item name, or the seed item name with a known suffix stripped, all sourced from
data/vendor/gardening_seed_items.json (the XIVAPI snapshot). data/aliases.json overlays hand
entries for labels that rule set does not reach, each carrying a reason.
"""
from __future__ import annotations

import csv
import json
import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
DATA_DIR = REPO_ROOT / "data"
VENDOR_DIR = DATA_DIR / "vendor"
SOURCES_DIR = DATA_DIR / "sources"

# Suffixes a seed item name carries that its offspring / parent label in other sources may omit.
SUFFIXES = [" Seeds", " Set", " Kernels", " Cloves", " Bud", " Bulbs"]

MONTHS = {
    "January", "February", "March", "April", "May", "June",
    "July", "August", "September", "October", "November", "December",
}

# Exact strings that pattern rules below cannot catch: a bare credited tester's name dropped into
# a cross cell alongside "TESTING" with no seed-name shape and no @/#/URL marker to key on.
KNOWN_NOISE = {"Hanji K."}


def load_json(path: Path) -> dict:
    return json.loads(path.read_text())


def load_vendor(name: str) -> dict:
    return load_json(VENDOR_DIR / name)


def load_aliases() -> dict:
    return load_json(DATA_DIR / "aliases.json")


def load_overrides() -> dict:
    path = DATA_DIR / "overrides.json"
    if not path.exists():
        return {"pairs": []}
    return load_json(path)


def outdoor_rows(seed_items: dict) -> list[dict]:
    return [r for r in seed_items["rows"] if not r["isPlantPotFlowerSeed"]]


def build_resolver(seed_items: dict, aliases: dict) -> dict[str, int]:
    """Map every seed item name, produce item name, and suffix-stripped seed item name to a row id.

    Restricted to outdoor rows: flowerpot flowers are out of scope everywhere in Gardener, so a
    label that only matches a flowerpot row must fail to resolve, not silently resolve to the wrong
    kind of seed.
    """
    resolver: dict[str, int] = {}
    for r in outdoor_rows(seed_items):
        row = r["row"]
        resolver.setdefault(r["seedItemName"], row)
        resolver.setdefault(r["produceItemName"], row)
        for suffix in SUFFIXES:
            if r["seedItemName"].endswith(suffix):
                resolver.setdefault(r["seedItemName"][: -len(suffix)], row)

    for label, entry in aliases.get("seedAliases", {}).items():
        resolver[label] = entry["row"]

    return resolver


def resolve_label(label: str, resolver: dict[str, int]) -> int | None:
    return resolver.get(label)


# ---------------------------------------------------------------------------
# trends-and-families.csv: bounded grid, legend/credit tokens, dead-cross marker
# ---------------------------------------------------------------------------


def read_matrix_rows(path: Path) -> list[list[str]]:
    with path.open(newline="", encoding="utf-8") as f:
        return list(csv.reader(f))


def matrix_axis(rows: list[list[str]]) -> list[tuple[int, str]]:
    """Column positions (== row positions, the grid is symmetric) carrying a seed label.

    Column/row 0 is the axis; family groups are separated by a blank column/row, which this
    filters out by requiring a non-empty label.
    """
    result = []
    for i in range(1, len(rows[0])):
        # A label can itself wrap onto a second line in the spreadsheet (e.g. "Allagan\nMelon");
        # normalize the same way a multi-offspring cell's individual tokens are normalized.
        label = " ".join(rows[0][i].split())
        if label:
            result.append((i, label))
    return result


def is_legend_noise(token: str) -> bool:
    """True for spreadsheet legend/credit text that a bounded-range parse still sweeps up.

    None of the 81 outdoor seed labels or their abbreviations collide with any of these shapes.
    """
    t = token.strip()
    if not t:
        return True
    if t in KNOWN_NOISE:
        return True
    if re.fullmatch(r"\d{4}", t):
        return True
    words = t.split()
    if words and all(w in MONTHS or re.fullmatch(r"\d{4}", w) for w in words):
        return True
    if re.fullmatch(r"Round:\s*\d+", t, re.IGNORECASE):
        return True
    if t.upper() == "TESTING":
        return True
    if t.upper().startswith("NOTE:"):
        return True
    lower = t.lower()
    if "@" in t or "#" in t or "http" in lower or ".com" in lower:
        return True
    if re.search(r"\b(thanks|community|contact info|creator)\b", t, re.IGNORECASE):
        return True
    return False


def split_cell(raw: str) -> list[str]:
    """Split a matrix cell into candidate tokens.

    A blank line separates distinct possible offspring; a single embedded newline mid-cell is
    spreadsheet word-wrap of one label (e.g. "Allagan\\nMelon", "Mandragora\\nQueen") and is
    joined back into one token, never treated as a second entry.
    """
    parts = re.split(r"\n\s*\n+", raw)
    tokens = []
    for part in parts:
        normalized = " ".join(part.split())
        if normalized:
            tokens.append(normalized)
    return tokens


class CellResult:
    """The parsed meaning of one matrix cell: dead, empty/no-data, or a set of offspring tokens."""

    __slots__ = ("dead", "tokens")

    def __init__(self, dead: bool, tokens: list[str]):
        self.dead = dead
        self.tokens = tokens


def parse_cell(raw: str) -> CellResult:
    if not raw.strip():
        return CellResult(dead=False, tokens=[])
    if raw.strip().upper() == "X":
        return CellResult(dead=True, tokens=[])

    tokens = split_cell(raw)
    kept = []
    dead = False
    for tok in tokens:
        if tok.lower() == "dead":
            dead = True
            continue
        if is_legend_noise(tok):
            continue
        kept.append(tok)
    return CellResult(dead=dead, tokens=kept)


def iter_matrix_pairs(rows: list[list[str]], axis: list[tuple[int, str]]):
    """Yield (grid_index_a, grid_index_b, CellResult) for each unordered pair in the bounded grid.

    Only the cell above the diagonal (grid index `ri < ci`, in the fixed row0/col0 axis order) is
    read. The two triangles are supposed to mirror each other, but for every one of the 3,240
    off-diagonal pairs the upper cell is populated and the lower one is not authoritative: 1,343
    lower cells are simply blank, and of the 1,897 that are non-blank, 635 are a bare 'X' whose
    mirrored upper cell names a real, nick75g-confirmed offspring (checked while writing this
    parser), against 2 pairs where the disagreement runs the other way. So the upper triangle is
    the entire signal; unioning both would fold a wrong 'X' into >600 otherwise-live pairs.
    """
    for idx, (ri, _) in enumerate(axis):
        for ci, _ in axis[idx + 1 :]:
            yield ri, ci, parse_cell(rows[ri][ci])
