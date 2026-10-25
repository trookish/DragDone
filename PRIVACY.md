# Privacy

DragDone runs entirely on your PC. It has no accounts, no telemetry, no analytics, and it makes no
network requests of its own.

## What it stores

Two small files, written only when you change something in Settings:

- `%APPDATA%\DragDone\settings.json` — your choices: action layout, accent, language, shelf state,
  startup preference, preview corner.
- `%APPDATA%\DragDone\theme.json` — a custom colour preset, if you saved one.

A packaged (Microsoft Store) build may have these writes redirected by Windows into the app's own
package folder. Either way the app reads and writes the same two files and nothing else.

Delete those files at any time to reset DragDone. Uninstalling asks before removing them and never
removes them unattended.

## What it reads

Only the files you drag onto it, and only to perform the action you chose. DragDone writes its
result next to the original with a collision-safe name and never overwrites the source.

## What leaves your machine

Nothing. Conversions, resizes, PDF splitting, Office document handling and image work all happen
locally using libraries inside the app, `Windows.Data.Pdf`, and — for video and audio — an FFmpeg
executable you already have installed. DragDone never downloads FFmpeg or anything else.

Custom actions you add in Settings start the program you named, on the files you dropped. DragDone
sends nothing to that program beyond the command line you wrote; what the program itself does is
up to the program.

If you added a file to the Shelf, the shelf remembers its path and shows a thumbnail. That list
stays on your machine.

## Crash information

If an action fails, DragDone writes a short message to `%APPDATA%\DragDone\error.log`. It is local
only and is not transmitted anywhere. It is excluded from version control and never uploaded.

## Contact

Open an issue at <https://github.com/trookish/DragDone/issues>.
