"""Captures every secondary window via `DragDone.exe --show <window> <lang> --capture <png>`.

The app renders its own client area off-screen and never activates, so the user's screen
and focus are untouched. Usage: python tools/capture_windows.py <exe> <out_dir>
"""
import os
import subprocess
import sys


def main():
    exe, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    for which in ("settings", "welcome", "crop", "rename", "shelf"):
        for lang in ("ar", "en"):
            path = os.path.abspath(os.path.join(out, f"{lang}-{which}.png"))
            if os.path.exists(path):
                os.remove(path)
            code = subprocess.run([exe, "--show", which, lang, "--capture", path], timeout=60).returncode
            print("captured" if os.path.exists(path) else "MISSING", path, "exit", code)


if __name__ == "__main__":
    main()
