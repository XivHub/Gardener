#!/usr/bin/env python3
"""Validate Gardener/Localization/Strings.resx and Strings.es.resx, and guard the composition
contract those files rely on.

--check fails non-zero on:
  1. a key in Strings.es.resx absent from Strings.resx;
  2. a slot-index-set mismatch between a neutral value and its Spanish value;
  3. a Strings.resx entry with an empty <comment>;
  4. a string literal or $"..." reaching the player as display text: the first argument to an
     ImGui text/widget call anywhere under Gardener/Windows/, or any $"..." anywhere under
     Gardener/Windows/, Gardener/Planner/, Helpers/Reminders.cs, Helpers/DtrEntry.cs or
     Game/SoilSources.cs. A pure ##/###-id suffix is not display text; an ImGui popup
     identifier (OpenPopup/BeginPopup, which never draws its own name) is not display text
     either, per the do-not-translate register in Strings.resx's own header comment.
  5. a composed soil name that carries no SoilFamily identifier on its own line: a string
     literal anywhere under Gardener/ matching "Grade {" or "} Topsoil" (see MainWindow.cs:517,
     historically, for the shape this catches that a plain SoilFamily grep misses).

Prints, never fails on, the count of neutral keys with no Spanish translation yet (per-key
fallback covers those at runtime).
"""
from __future__ import annotations

import argparse
import html
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
LOC_DIR = REPO_ROOT / "Gardener" / "Localization"
NEUTRAL_RESX = LOC_DIR / "Strings.resx"
SPANISH_RESX = LOC_DIR / "Strings.es.resx"

DATA_RE = re.compile(
    r'<data name="([^"]+)"[^>]*>\s*<value>(.*?)</value>(?:\s*<comment>(.*?)</comment>)?\s*</data>',
    re.S,
)
SLOT_RE = re.compile(r"\{(\d+)\}")
HOLE_RE = re.compile(r"\{[^{}]*\}")
ID_TOKEN_RE = re.compile(r"#{2,3}[\w\-]*")
LETTER_RE = re.compile(r"[A-Za-zÀ-ÖØ-öø-ÿ]")

# ImGui calls whose first string argument (TextColored's second) is rendered as visible text to
# the player, as opposed to a widget/window/table identifier (BeginTable, PushID, OpenPopup,
# BeginPopup - see the do-not-translate register for why popup ids are excluded).
IMGUI_TEXT_FUNCS = [
    "Text", "TextUnformatted", "TextWrapped", "TextDisabled", "BulletText",
    "Button", "SmallButton", "ArrowButton", "Checkbox", "RadioButton", "Selectable",
    "CollapsingHeader", "BeginTabItem", "TableSetupColumn", "Combo", "InputText",
    "InputTextWithHint", "SliderFloat", "SliderInt", "InputInt", "InputFloat",
    "MenuItem", "TreeNode", "LabelText",
]

STR_LIT = r'\$?"(?:[^"\\]|\\.)*"'
IMGUI_CALL_RE = re.compile(
    r"ImGui\.(" + "|".join(IMGUI_TEXT_FUNCS) + r")\(\s*(" + STR_LIT + r")"
)
IMGUI_TEXTCOLORED_RE = re.compile(r"ImGui\.TextColored\([^,]+,\s*(" + STR_LIT + r")")
INTERP_LIT_RE = re.compile(r'\$"(?:[^"\\]|\\.)*"')

# Popup identifiers never draw their own text (BeginPopup/OpenPopup), so raw English there is an
# identifier, not copy - the do-not-translate register in Strings.resx's header names this
# exception explicitly.
POPUP_CONTEXT_RE = re.compile(r"\b\w*[Pp]opup\w*\s*[(=]")

# PushID/GetID/PopID scope the ImGui id stack; the string never renders as visible text.
ID_STACK_CONTEXT_RE = re.compile(r"\bImGui\.(PushID|GetID|PopID)\s*\(")

# SoilSources.ClockHour renders an Eorzean clock hour (a game-clock fact, not a real-world time)
# as an invariant "7am"/"7pm" label in every language - its own doc comment is the reason this
# never routes through Loc.
CLOCK_SUFFIXES = {"am", "pm"}

# Task 3.3: an example URL is not player-facing copy.
EXEMPT_LITERALS = {"e.g. http://127.0.0.1:9999/log"}

SCAN_PATHS = [
    REPO_ROOT / "Gardener" / "Windows",
    REPO_ROOT / "Gardener" / "Planner",
    REPO_ROOT / "Gardener" / "Helpers" / "Reminders.cs",
    REPO_ROOT / "Gardener" / "Helpers" / "DtrEntry.cs",
    REPO_ROOT / "Gardener" / "Game" / "SoilSources.cs",
]


def iter_cs_files(paths):
    for p in paths:
        if p.is_dir():
            yield from sorted(p.glob("*.cs"))
        elif p.exists():
            yield p


def parse_resx(path: Path):
    content = path.read_text(encoding="utf-8")
    entries = {}
    for name, value, comment in DATA_RE.findall(content):
        entries[name] = (html.unescape(value), html.unescape(comment) if comment else "")
    return entries


def slot_indices(value: str) -> set[int]:
    return {int(m) for m in SLOT_RE.findall(value)}


def strip_literal(raw: str) -> str:
    """Strip a leading $ and the surrounding quotes from a matched string-literal token."""
    if raw.startswith("$"):
        raw = raw[1:]
    return raw[1:-1]


def is_display_text(content: str) -> bool:
    """content: a string literal's inner text (interpolation holes still present as {...}).
    True if what remains after removing holes and pure id-suffix tokens still carries real
    prose the player would see untranslated."""
    if content in EXEMPT_LITERALS:
        return False
    without_holes = HOLE_RE.sub("", content)
    without_ids = ID_TOKEN_RE.sub("", without_holes).strip()
    if without_ids in CLOCK_SUFFIXES:
        return False
    return bool(LETTER_RE.search(without_ids))


def check_imgui_calls(path: Path, violations: list):
    for lineno, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        stripped = line.strip()
        if stripped.startswith("//"):
            continue
        for m in IMGUI_CALL_RE.finditer(line):
            lit = strip_literal(m.group(2))
            if is_display_text(lit):
                violations.append((path, lineno, line.strip(), f"ImGui.{m.group(1)} first argument"))
        for m in IMGUI_TEXTCOLORED_RE.finditer(line):
            lit = strip_literal(m.group(1))
            if is_display_text(lit):
                violations.append((path, lineno, line.strip(), "ImGui.TextColored text argument"))


def check_interpolated_strings(path: Path, violations: list):
    for lineno, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        stripped = line.strip()
        if stripped.startswith("//"):
            continue
        if POPUP_CONTEXT_RE.search(line) or ID_STACK_CONTEXT_RE.search(line):
            continue
        for m in INTERP_LIT_RE.finditer(line):
            lit = strip_literal(m.group(0))
            if is_display_text(lit):
                violations.append((path, lineno, line.strip(), '$"..." interpolated string'))


GRADE_TOPSOIL_RE = re.compile(r'"[^"]*(?:Grade \{|\} Topsoil)')


def check_composed_soil_names(violations: list):
    for path in sorted((REPO_ROOT / "Gardener").rglob("*.cs")):
        for lineno, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
            if line.strip().startswith("//"):
                continue
            if GRADE_TOPSOIL_RE.search(line):
                violations.append((path, lineno, line.strip(), 'composed "Grade {"/"} Topsoil" literal'))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="run every check and exit non-zero on failure")
    args = parser.parse_args()
    if not args.check:
        parser.print_help()
        return 0

    failures: list[str] = []

    neutral = parse_resx(NEUTRAL_RESX)
    spanish = parse_resx(SPANISH_RESX)

    # 1. Spanish key absent from neutral.
    orphan_keys = sorted(set(spanish) - set(neutral))
    for key in orphan_keys:
        failures.append(f"Strings.es.resx has key '{key}' not present in Strings.resx")

    # 2. Slot-index mismatch between a neutral value and its Spanish translation.
    for key, (es_value, _) in spanish.items():
        if key not in neutral:
            continue
        neutral_value, _ = neutral[key]
        neutral_slots = slot_indices(neutral_value)
        es_slots = slot_indices(es_value)
        if neutral_slots != es_slots:
            failures.append(
                f"{key}: slot mismatch, neutral={sorted(neutral_slots)} es={sorted(es_slots)} "
                f"(EN: {neutral_value!r}, ES: {es_value!r})"
            )

    # 3. Empty <comment> in the neutral resx.
    for key, (_, comment) in neutral.items():
        if not comment.strip():
            failures.append(f"Strings.resx entry '{key}' has an empty <comment>")

    # 4. Display text reaching an ImGui call, or a $"..." producing display text, across the
    #    five paths the acceptance criteria's own grep covers.
    imgui_violations: list = []
    for path in iter_cs_files([REPO_ROOT / "Gardener" / "Windows"]):
        check_imgui_calls(path, imgui_violations)
    for path in iter_cs_files(SCAN_PATHS):
        check_interpolated_strings(path, imgui_violations)
    seen = set()
    for path, lineno, line, reason in imgui_violations:
        key = (path, lineno)
        if key in seen:
            continue
        seen.add(key)
        rel = path.relative_to(REPO_ROOT)
        failures.append(f"{rel}:{lineno}: untranslated display text via {reason}: {line}")

    # 5. A composed "Grade {N} <family> Topsoil"-shaped literal anywhere under Gardener/.
    soil_violations: list = []
    check_composed_soil_names(soil_violations)
    for path, lineno, line, reason in soil_violations:
        rel = path.relative_to(REPO_ROOT)
        failures.append(f"{rel}:{lineno}: {reason}: {line}")

    # Informational only: neutral keys with no Spanish translation yet (per-key fallback covers
    # these at runtime, so this never fails the build).
    missing = sorted(set(neutral) - set(spanish))
    print(f"{len(missing)} neutral key(s) have no Spanish translation yet.")

    if failures:
        print(f"check_loc: {len(failures)} failure(s):", file=sys.stderr)
        for f in failures:
            print(f"  {f}", file=sys.stderr)
        return 1

    print(f"check_loc: OK ({len(neutral)} neutral keys, {len(spanish)} Spanish keys).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
