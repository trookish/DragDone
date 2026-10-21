"""
End-to-end drag test: a real Explorer window, real injected mouse and keyboard input.

Needs: uv run --no-project --with uiautomation --with pillow python tools/e2e_drag.py ...

Phase 1 (default): press on the file, drag past the threshold, press the trigger key,
  capture the screen, then wait for --target (x,y screen pixels) or --wait-file.
Phase 2: move onto the target, capture the hover state, release, wait for output files.

Input is always released in `finally`, so a failed run cannot leave a button or key stuck.
"""
from __future__ import annotations

import argparse
import ctypes
import json
import os
import subprocess
import sys
import time
from ctypes import wintypes

import uiautomation as auto
from PIL import ImageGrab

ctypes.windll.shcore.SetProcessDpiAwareness(2)  # physical pixels everywhere
user32 = ctypes.windll.user32

INPUT_MOUSE, INPUT_KEYBOARD = 0, 1
MOVE, LEFTDOWN, LEFTUP, ABSOLUTE, VIRTUALDESK = 0x1, 0x2, 0x4, 0x8000, 0x4000
MIDDLEDOWN, MIDDLEUP, RIGHTDOWN, RIGHTUP = 0x20, 0x40, 0x8, 0x10
KEYUP = 0x2
VK = {"Shift": 0x10, "Ctrl": 0x11, "Alt": 0x12, "Win": 0x5B, "Middle": 0, "Right": 0}


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wintypes.LONG), ("dy", wintypes.LONG), ("mouseData", wintypes.DWORD),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD), ("dwFlags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


class _U(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wintypes.DWORD), ("u", _U)]


def _send(inp: INPUT) -> None:
    user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))


def _virtual() -> tuple[int, int, int, int]:
    return (user32.GetSystemMetrics(76), user32.GetSystemMetrics(77),
            user32.GetSystemMetrics(78), user32.GetSystemMetrics(79))


def mouse_move(x: int, y: int) -> None:
    vx, vy, vw, vh = _virtual()
    nx = round((x - vx) * 65535 / (vw - 1))
    ny = round((y - vy) * 65535 / (vh - 1))
    i = INPUT(INPUT_MOUSE)
    i.u.mi = MOUSEINPUT(nx, ny, 0, MOVE | ABSOLUTE | VIRTUALDESK, 0, 0)
    _send(i)


def mouse_button(down: bool, button: str = "left") -> None:
    flag = {"left": LEFTDOWN if down else LEFTUP,
            "middle": MIDDLEDOWN if down else MIDDLEUP,
            "right": RIGHTDOWN if down else RIGHTUP}[button]
    i = INPUT(INPUT_MOUSE)
    i.u.mi = MOUSEINPUT(0, 0, 0, flag, 0, 0)
    _send(i)


def key(name: str, down: bool) -> None:
    if name in ("Middle", "Right"):
        mouse_button(down, name.lower())
        return
    i = INPUT(INPUT_KEYBOARD)
    i.u.ki = KEYBDINPUT(VK[name], 0, 0 if down else KEYUP, 0, 0)
    _send(i)


def glide(x0: int, y0: int, x1: int, y1: int, steps: int = 14, delay: float = 0.012) -> None:
    for s in range(1, steps + 1):
        mouse_move(round(x0 + (x1 - x0) * s / steps), round(y0 + (y1 - y0) * s / steps))
        time.sleep(delay)


def cursor() -> tuple[int, int]:
    p = wintypes.POINT()
    user32.GetCursorPos(ctypes.byref(p))
    return p.x, p.y


def shot(path: str) -> None:
    # The overlay and Explorer's drag ghost are layered windows; without this flag they are invisible.
    ImageGrab.grab(all_screens=True, include_layered_windows=True).save(path)


def explorer_windows_for(folder: str) -> list[int]:
    """Handles of visible Explorer windows currently showing `folder`.

    Explorer's title is the folder name alone, so match on that; the full path is used to
    disambiguate when several folders share a name.
    """
    want = os.path.basename(os.path.normpath(folder)).lower()
    handles: list[int] = []
    proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def cb(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        buf = ctypes.create_unicode_buffer(512)
        user32.GetWindowTextW(hwnd, buf, 512)
        if buf.value.strip().lower() == want:
            handles.append(hwnd)
        return True

    user32.EnumWindows(proc(cb), 0)
    return handles


def process_windows(pid: int) -> list[dict]:
    found: list[dict] = []
    proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def cb(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            r = wintypes.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(r))
            ex = user32.GetWindowLongW(hwnd, -20)
            found.append({"hwnd": hwnd, "rect": [r.left, r.top, r.right, r.bottom],
                          "topmost": bool(ex & 0x8), "noactivate": bool(ex & 0x08000000),
                          "layered": bool(ex & 0x80000)})
        return True

    user32.EnumWindows(proc(cb), 0)
    return found


def dragdone_pid() -> int | None:
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq DragDone.exe", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True).stdout
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) > 1 and parts[0].strip('"').lower() == "dragdone.exe":
            return int(parts[1])
    return None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", required=True)
    ap.add_argument("--dir", required=True, help="scratch folder holding the file to drag")
    ap.add_argument("--file", default="test")
    ap.add_argument("--key", default="Shift", choices=list(VK))
    ap.add_argument("--out", required=True, help="folder for screenshots and report")
    ap.add_argument("--target", help="x,y screen pixels to drop on; omit to stop after the menu appears")
    ap.add_argument("--wait-file", help="poll this file for 'x,y' once the menu is up")
    ap.add_argument("--hold-key-on-drop", action="store_true")
    ap.add_argument("--at", help="x,y screen pixels for the Explorer window; pick an uncovered area")
    ap.add_argument("--burst", action="store_true", help="capture timed region frames right after the key press")
    ap.add_argument("--click-after", help="x,y to double-click in Explorer while the result card is still showing")
    ap.add_argument("--sector", help="action id to drop on, e.g. convert:jpg (uses the app's own trace)")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    report: dict = {"steps": []}

    def note(msg: str, **kw) -> None:
        report["steps"].append({"t": round(time.time() % 1000, 2), "msg": msg, **kw})
        print(msg, kw or "", flush=True)

    if dragdone_pid() is None:
        subprocess.Popen([a.exe, "--tray"])
        time.sleep(2.5)
    pid = dragdone_pid()
    note("dragdone", pid=pid)
    if pid is None:
        print(json.dumps(report, indent=1))
        return 2

    a.dir = os.path.normpath(a.dir)  # explorer.exe silently opens the wrong folder for "/" paths
    before = set(os.listdir(a.dir))
    # Close any Explorer already showing this folder: a stale window behind another one makes
    # the harness pick the wrong handle and then refuse to send input.
    for h in explorer_windows_for(a.dir):
        user32.PostMessageW(h, 0x0010, 0, 0)  # WM_CLOSE
    time.sleep(0.6)
    subprocess.Popen(["explorer.exe", a.dir])
    window = auto.WindowControl(searchDepth=1, ClassName="CabinetWClass", SubName=os.path.basename(a.dir.rstrip("/\\")))
    if not window.Exists(10, 0.5):
        note("explorer window not found")
        return 3
    window.SetActive()
    time.sleep(0.8)

    # Never aim input blind at the user's live desktop: pin Explorer on top at a known spot, then
    # prove with WindowFromPoint that the press point belongs to it before anything is sent.
    hwnd = window.NativeWindowHandle
    user32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                                    ctypes.c_int, ctypes.c_int, wintypes.UINT]
    user32.WindowFromPoint.argtypes = [wintypes.POINT]
    user32.WindowFromPoint.restype = wintypes.HWND
    user32.GetAncestor.argtypes = [wintypes.HWND, wintypes.UINT]
    user32.GetAncestor.restype = wintypes.HWND
    mx, my, _, _ = _virtual()
    wx, wy = (int(v) for v in a.at.split(",")) if a.at else (mx + 120, my + 120)
    user32.ShowWindow(hwnd, 9)  # SW_RESTORE: a minimized or maximized Explorer ignores SetWindowPos
    set_ok = user32.SetWindowPos(hwnd, ctypes.c_void_p(-1), wx, wy, 960, 640, 0x0040)  # TOPMOST|SHOWWINDOW
    time.sleep(0.8)
    # Explorer can ignore the move while it is still restoring; check and retry.
    for _ in range(6):
        er = wintypes.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(er))
        if abs(er.left - wx) < 40 and abs(er.top - wy) < 40:
            break
        user32.ShowWindow(hwnd, 9)
        set_ok = user32.SetWindowPos(hwnd, ctypes.c_void_p(-1), wx, wy, 960, 640, 0x0040)
        time.sleep(0.5)
    item = window.ListItemControl(SubName=a.file)
    if not item.Exists(8, 0.5):
        note("file item not found")
        return 4
    r = item.BoundingRectangle
    sx, sy = (r.left + r.right) // 2, (r.top + r.bottom) // 2
    top = user32.GetAncestor(user32.WindowFromPoint(wintypes.POINT(sx, sy)), 2)
    if top != hwnd:
        er = wintypes.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(er))
        ex_style = user32.GetWindowLongW(hwnd, -20)
        note("ABORT: press point is not over Explorer; no input sent", under=top, explorer=hwnd, at=[sx, sy],
             explorer_rect=[er.left, er.top, er.right, er.bottom], explorer_topmost=bool(ex_style & 8),
             explorer_iconic=bool(user32.IsIconic(hwnd)), explorer_visible=bool(user32.IsWindowVisible(hwnd)),
             set_pos_ok=bool(set_ok), last_error=ctypes.get_last_error())
        user32.SetWindowPos(hwnd, ctypes.c_void_p(-2), 0, 0, 0, 0, 0x0003 | 0x0010)  # NOTOPMOST
        return 5
    note("file item", at=[sx, sy], rect=[r.left, r.top, r.right, r.bottom])

    down = False
    held = False
    try:
        mouse_move(sx, sy)
        time.sleep(0.25)
        mouse_button(True)
        down = True
        time.sleep(0.15)
        glide(sx, sy, sx + 70, sy + 50)
        time.sleep(0.35)
        key(a.key, True)
        held = True
        if a.burst:
            t0 = time.time()
            for i, delay in enumerate((0.08, 0.2, 0.4, 0.7, 1.2, 2.0)):
                time.sleep(max(0, t0 + delay - time.time()))
                bx, by = cursor()
                ImageGrab.grab(bbox=(bx - 330, by - 300, bx + 330, by + 330), all_screens=True,
                               include_layered_windows=True).save(os.path.join(a.out, f"burst-{i}-{int(delay*1000)}ms.png"))
            note("burst captured")
        else:
            time.sleep(0.7)
        cx, cy = cursor()
        shot(os.path.join(a.out, "1-menu.png"))
        wins = process_windows(pid)
        note("menu shown", cursor=[cx, cy], windows=wins)

        target = None
        if a.target:
            target = tuple(int(v) for v in a.target.split(","))
        elif a.wait_file:
            deadline = time.time() + 120
            while time.time() < deadline and target is None:
                if os.path.exists(a.wait_file):
                    text = open(a.wait_file).read().strip()
                    if text:
                        target = tuple(int(v) for v in text.split(","))
                time.sleep(0.4)
        if target is not None:
            # Releasing a file over some other app would drop it into that app. Only ever release
            # inside a DragDone window; otherwise abandon with Escape.
            live = process_windows(pid)
            if not any(w["rect"][0] <= target[0] < w["rect"][2] and w["rect"][1] <= target[1] < w["rect"][3] for w in live):
                note("target is not inside any DragDone window; abandoning drag", target=list(target), windows=live)
                target = None
        if target is None and a.sector:
            # The app reports where each sector landed on screen; use that instead of guessing.
            trace = os.environ.get("DRAGDONE_TRACE")
            if trace and os.path.exists(trace):
                for line in open(trace, encoding="utf-8", errors="ignore").read().splitlines():
                    if f"sector " in line and f" {a.sector} " in line:
                        at = line.rsplit(" at ", 1)[-1]
                        target = tuple(int(v) for v in at.split(","))
                        note("sector from trace", sector=a.sector, target=list(target))
                        break
        if target is None:
            note("no target; abandoning drag with Escape")
            user32.keybd_event(0x1B, 0, 0, 0)
            user32.keybd_event(0x1B, 0, KEYUP, 0)
            time.sleep(0.4)
        else:
            glide(cx, cy, target[0], target[1], steps=18)
            time.sleep(0.5)
            shot(os.path.join(a.out, "2-hover.png"))
            if not a.hold_key_on_drop:
                key(a.key, False)
                held = False
                time.sleep(0.2)
            mouse_button(False)
            down = False
            note("dropped", at=list(target))
            time.sleep(0.5)
            shot(os.path.join(a.out, "3-progress.png"))
            time.sleep(2.5)
            shot(os.path.join(a.out, "4-result.png"))
            if a.click_after:
                # While the result card is still up, click an Explorer item that lies under the overlay's
                # transparent area. If the overlay swallows the click, the folder will not change.
                cx2, cy2 = (int(v) for v in a.click_after.split(","))
                before_title = window.Name
                mouse_move(cx2, cy2)
                time.sleep(0.2)
                mouse_button(True)
                mouse_button(False)
                time.sleep(0.15)
                mouse_button(True)
                mouse_button(False)  # double-click: navigates into a folder item
                time.sleep(1.0)
                note("click-through", before=before_title, after=window.Name, changed=before_title != window.Name,
                     hwnd_under=user32.GetAncestor(user32.WindowFromPoint(wintypes.POINT(cx2, cy2)), 2))
    finally:
        if down:
            mouse_button(False)
        if held:
            key(a.key, False)
        # A stuck modifier or mouse button would poison every later run, so clear them all.
        for name in ("Shift", "Ctrl", "Alt", "Win"):
            key(name, False)
        for btn in ("left", "middle", "right"):
            mouse_button(False, btn)

    time.sleep(1.0)
    after = set(os.listdir(a.dir))
    report["created"] = sorted(after - before)
    report["dragdone_alive"] = dragdone_pid() == pid
    try:
        user32.SetWindowPos(hwnd, ctypes.c_void_p(-2), 0, 0, 0, 0, 0x0003 | 0x0010)  # NOTOPMOST
        window.GetWindowPattern().Close()
    except Exception:  # noqa: BLE001 - best-effort cleanup of the scratch window
        pass
    note("done", created=report["created"], alive=report["dragdone_alive"])
    open(os.path.join(a.out, "report.json"), "w", encoding="utf-8").write(json.dumps(report, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
