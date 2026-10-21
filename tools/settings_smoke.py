"""
Settings smoke test: open the settings window and change things, the way a person would.

Needs: uv run --no-project --with uiautomation --with pillow python tools/settings_smoke.py <exe>

Every control in the settings window is built in code and the page is rebuilt whenever the
palette changes, so a single control that throws used to leave the window with no content at
all. This walks the dropdowns, switches the theme back and forth, and fails if the page loses
its controls, goes blank, or if the app logs a crash while doing it.

Exits 0 when the page survived every change, 1 otherwise.
"""
from __future__ import annotations
import argparse
import os
import subprocess
import sys
import tempfile
import time

import uiautomation as auto
from PIL import Image

TITLE = "DragDone Settings"
CONTROL_TYPES = ("ComboBoxControl", "ButtonControl", "SliderControl", "CheckBoxControl")


def shoot(win, path):
    win.CaptureToImage(path)
    return path


def content_ratio(path):
    """Share of pixels that differ from the most common tone; a blank page scores near zero."""
    im = Image.open(path).convert("L")
    hist = im.histogram()
    background = hist.index(max(hist))
    marked = sum(1 for p in im.get_flattened_data() if abs(p - background) > 24)
    return marked / (im.width * im.height)


def count_controls(ctrl, depth=0):
    if depth > 14:
        return 0
    total = 0
    for kid in ctrl.GetChildren():
        if kid.ControlTypeName in CONTROL_TYPES:
            total += 1
        total += count_controls(kid, depth + 1)
    return total


def dropdowns(ctrl, depth=0, found=None):
    found = [] if found is None else found
    if depth > 14:
        return found
    for kid in ctrl.GetChildren():
        if kid.ControlTypeName == "ComboBoxControl":
            found.append(kid)
        dropdowns(kid, depth + 1, found)
    return found


def items_of(combo):
    combo.GetExpandCollapsePattern().Expand()
    time.sleep(0.45)
    items = [k for k in combo.GetChildren() if k.ControlTypeName in ("ListItemControl", "MenuItemControl")]
    names = [k.Name for k in items]
    return items, names


def select(win, wanted):
    """Picks `wanted` from whichever dropdown offers it. Returns True when it was chosen."""
    for combo in dropdowns(win):
        try:
            items, names = items_of(combo)
            if wanted not in names:
                combo.GetExpandCollapsePattern().Collapse()
                time.sleep(0.15)
                continue
            match = next(k for k in items if k.Name == wanted)
            match.Click(simulateMove=False)
            return True
        except Exception:
            continue
    return False


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("exe", help="path to DragDone.exe")
    ap.add_argument("--shots", help="keep screenshots in this folder")
    ap.add_argument("--theme", action="append", default=None,
                    help="a dropdown value to select; repeatable, defaults to the light/dark cycle")
    args = ap.parse_args()

    shots = args.shots or tempfile.mkdtemp(prefix="dragdone-settings-")
    os.makedirs(shots, exist_ok=True)

    log = os.path.join(os.environ.get("APPDATA", tempfile.gettempdir()), "DragDone", "error.log")
    before_log = os.path.getsize(log) if os.path.exists(log) else 0

    subprocess.run(["taskkill", "/F", "/IM", os.path.basename(args.exe)], capture_output=True)
    time.sleep(1)
    proc = subprocess.Popen([args.exe, "--settings"])
    failures = []
    try:
        time.sleep(8)
        win = auto.WindowControl(searchDepth=1, Name=TITLE)
        if not win.Exists(5):
            print(f"FAIL  no window titled {TITLE!r}")
            return 1

        win.SetActive()
        time.sleep(0.5)
        base_shot = shoot(win, os.path.join(shots, "0-baseline.png"))
        base_controls = count_controls(win)
        base_ratio = content_ratio(base_shot)
        print(f"baseline: controls={base_controls} content={base_ratio:.3f}")
        if base_controls < 20:
            print("FAIL  the settings page is not built at all")
            return 1

        exercised = 0
        for step, wanted in enumerate(args.theme or ["Light", "Dark", "Follow Windows", "Light"], start=1):
            if not select(win, wanted):
                # WPF realises only the popup items it currently needs, so a value can be
                # genuinely absent or merely not exposed to automation. Neither is a defect.
                print(f"  {wanted:<16} SKIP  not offered to automation")
                continue
            exercised += 1
            time.sleep(1.6)
            path = shoot(win, os.path.join(shots, f"{step}-{wanted.replace(' ', '-').lower()}.png"))
            controls = count_controls(win)
            ratio = content_ratio(path)
            # A palette switch legitimately changes the overall brightness, so only a collapse
            # in the control count or a near-empty page counts as a failure.
            verdict = "ok"
            if controls < base_controls * 0.9:
                verdict = "LOST CONTROLS"
                failures.append(f"{wanted!r}: {controls} controls left, was {base_controls}")
            elif ratio < 0.005:
                verdict = "BLANK"
                failures.append(f"{wanted!r}: page went blank (content {ratio:.4f})")
            print(f"  {wanted:<16} {verdict:<14} controls={controls} content={ratio:.3f}")

        if exercised == 0:
            failures.append("no setting could be changed at all")

        if not win.Exists(1):
            failures.append("the window closed itself")

        grew = os.path.getsize(log) - before_log if os.path.exists(log) else 0
        if grew > 0:
            with open(log, encoding="utf-8", errors="replace") as f:
                f.seek(before_log)
                tail = f.read()[:600]
            failures.append(f"{grew} bytes of new crash output")
            print("\nnew entries in error.log:")
            print(tail)
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()

    print()
    if failures:
        for f in failures:
            print("FAIL ", f)
        print(f"shots: {shots}")
        return 1
    print(f"settings survived every change. shots: {shots}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
