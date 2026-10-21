#!/usr/bin/env bash
# Runs every non-interactive action headless against generated samples and prints a table.
# Usage: tools/smoke.sh  (from the repo root, after `dotnet build`)
set -u
ROOT="$(pwd -W)"   # native Windows path; the exe does not understand /c/...
EXE="$ROOT/src/DragDone/bin/Debug/net10.0-windows10.0.19041.0/DragDone.exe"
DIR="$ROOT/artifacts/run"
cd "$DIR" || exit 1
pass=0; fail=0
run() {
  local id="$1"; shift
  local report="$DIR/report.json"
  rm -f "$report"
  "$EXE" --run "$id" "$@" --report "$report" --lang en >"$DIR/last.log" 2>&1
  local code=$?
  local outcome detail
  outcome=$(python -c "import json,sys;d=json.load(open(sys.argv[1],encoding='utf-8'));print(d['outcome'])" "$report" 2>/dev/null || echo "no-report")
  detail=$(python -c "import json,sys,os;d=json.load(open(sys.argv[1],encoding='utf-8'));o=d['outputs'];print(', '.join(os.path.basename(x)+(' (%dKB)'%(os.path.getsize(x)//1024) if os.path.isfile(x) else '') for x in o) or (d.get('detail') or d['message']))" "$report" 2>/dev/null)
  if [ "$code" = 0 ]; then pass=$((pass+1)); s=OK; else fail=$((fail+1)); s=FAIL; [ "$outcome" = no-report ] && detail=$(grep -m1 -E "Exception|error" "$DIR/last.log"); fi
  printf '%-5s %-26s %-10s %s\n' "$s" "$id" "$outcome" "${detail:0:110}"
}
run convert:jpg photo.png
run convert:webp photo.png
run convert:bmp photo.jpg
run convert:pdf photo.jpg
run convert:ico photo.png
run img.compress photo.png
run img.resize:50% photo.png
run img.resize:800 photo.jpg
run img.rotate:90 photo.jpg
run img.flip:h photo.png
run img.crop:16x9 photo.png
run img.crop:1x1 photo.jpg
run img.crop:frac=0.1,0.1,0.5,0.5 photo.png
run img.crop:auto photo.png
run img.strip photo.jpg
run img.mergepdf photo.png photo.jpg
run convert:mp3 clip.mp4
run convert:webm clip.mp4
run convert:gif clip.mp4
run vid.compress:balanced clip.mp4
run vid.extract clip.mp4
run vid.mute clip.mp4
run vid.snapshot clip.mp4
run vid.rotate:90 clip.mp4
run vid.resize:480 clip.mp4
run convert:mp3 voice.wav
run convert:flac voice.wav
run aud.compress voice.wav
run aud.normalize voice.wav
run aud.trim voice.wav
run aud.mono voice.wav
run convert:pdf notes.txt
run file.zip photo.png notes.txt
run file.hash photo.png
run act.copypath photo.png notes.txt
# PDF and archive round trips use outputs created above.
run convert:png "photo.pdf"
run pdf.merge "photo.pdf" "photo - merged.pdf"
run pdf.split "photo - merged.pdf"
run pdf.rotate:90 "photo.pdf"
run pdf.compress "photo - merged.pdf"
run pdf.strip "photo.pdf"
run convert:jpg "photo - merged.pdf"
run arc.extract "photo - and 1 more.zip"
# Collision guard: converting twice must never overwrite.
run convert:webp photo.png
echo "passed=$pass failed=$fail"
