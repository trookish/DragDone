# Contributing

DragDone is a small, offline Windows utility. Contributions are welcome, especially new actions,
new themes, and fixes for behaviour on Windows 10.

## Before you start

```bash
dotnet build DragDone.sln          # WPF app + tests, .NET 10 SDK
dotnet test DragDone.sln           # domain tests
bash tools/smoke.sh                # 44 real actions on generated media
```

Run the tray app with `src/DragDone/bin/Debug/net10.0-windows10.0.19041.0/DragDone.exe`. Close the
welcome window and it stays in the tray; the first launch adds a startup entry, which you can turn
off in Settings.

Two rules that outrank everything else:

- **Nothing leaves the machine.** No uploads, no telemetry, no automatic downloads. FFmpeg and
  Office are used only when they are already installed, and the UI says so when they are not.
- **Nothing is overwritten.** Every output goes through `Core/OutputPaths`.

## Adding an action

1. Add the sector in `Core/ActionCatalog.cs`, in the menu that suits it. `ActionCatalog.MaxSectors`
   is 8 — beyond that the ring becomes unreadable, so group related actions under a parent with
   `Children` and a `DefaultChildId`.
2. Give it an `Id`, a `Glyph` from `Core/Glyphs.cs`, and either a `LabelKey` or a `Literal` name.
   Disabled states use `DisabledReasonKey`, never removal: the user should see the action and read
   why it is unavailable.
3. Handle the id in `Core/ActionExecutor.cs` and put the work in the matching `Processors/*` file.
   Interactive actions (`act.share`, `act.shelf`, `act.terminal`, `img.crop:free`,
   `act.move:browse`) are listed in `ActionExecutor.IsInteractive`; everything else is a job and
   must return a `JobResult` with its outputs so Undo works.
4. Add `action.<id>` (and `hint.<id>` when useful) to **both** `Localization/ar.json` and
   `Localization/en.json`. Arabic is the default language, so an English-only string is a bug.
5. `dotnet test` and `bash tools/smoke.sh` must both pass. A new action needs a line in
   `tools/smoke.sh` so it is exercised for real.

## Adding a theme

See [docs/THEMES.md](THEMES.md). A preset is a `Preset` entry in `UI/Theme.cs` plus two catalogue
keys in each language; a shared theme is just a `theme.json`.

## Style

- C# 13, nullable enabled, file-scoped namespaces, `var` where the type is obvious.
- UI is built in code (`SettingsWindow` and friends) so both directions share one layout. Use
  `Loc.T` for every visible string and `Theme.Current` for every colour — no literals.
- Comments explain *why*, and only where the reason is not obvious from the code.
- Prefer a pure function for anything with rules: if it can be tested without a window, it should
  be. `DragStateMachine`, `RadialMenu.Measure` and `OutputPaths` are the model to copy.
- Snapshots are part of the review: `DragDone.exe --snapshot artifacts/snap` renders 90 states.
  If a change alters the look, say which states changed and attach one.

## Pull requests

- One topic per PR, with the reason in the first paragraph.
- Say what you ran (`dotnet test`, `smoke.sh`, which snapshot), and what you could not test — for
  example Office conversion or Nearby Sharing without the hardware.
- Do not bump dependency licences casually. PDFsharp is pinned to 6.2.4 because it is the last MIT
  release; anything newer changes the licence of the whole project.
