# DragDone

[![build](https://github.com/trookish/DragDone/actions/workflows/build.yml/badge.svg)](https://github.com/trookish/DragDone/actions/workflows/build.yml)
[![license: MIT](https://img.shields.io/badge/license-MIT-2ea44f.svg)](LICENSE)
![platform: Windows 10/11](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4)

**Drag • Press • Done** — اسحب ملفًا، اضغط مفتاحًا، وانتهى.

DragDone is a small Windows 10/11 tray utility that belongs to whoever configures it. While you
drag files on the Desktop or in File Explorer, press a key and a radial menu opens under the cursor
with actions for those files. Drop onto an action and the result is saved next to the original.
Everything runs offline, and the interface follows the language Windows is showing — Arabic
(right-to-left) or English — unless you pin one.

<p>
  <img src="docs/images/ar-convert-donut.png" width="250" alt="Convert menu for an image, Arabic, donut layout">
  <img src="docs/images/en-convert-orbit.png" width="250" alt="Convert menu, English, orbit layout">
  <img src="docs/images/ar-convert-pie.png" width="250" alt="Convert menu, Arabic, paper theme, solid wheel">
</p>

Three of the shapes it can take, from the same menu: a donut ring, an orbit of badges around an open
centre, and a solid wheel. Labels, density, colours, material and size are all yours to set.

## Make it yours

Nothing about the look is hard-coded. Open **Settings** from the tray icon:

| | |
|---|---|
| **Character** | seven presets — Windows, Fluent, Paper, Graphite, Terminal, Vivid, Contrast — then adjust anything |
| **Shape** | Donut, solid wheel, orbit, or automatic; diameter, gap, outline thickness, hover growth |
| **Density** | compact, roomy, large — spacing and type size together |
| **Names** | automatic, symbols, symbols and names, or text only; rotate outside labels or keep them upright |
| **Colour** | follow the Windows accent, pick a named colour, or choose any colour; accent strength and contrast |
| **Material** | translucent surface, backdrop blur, light / dark / follow Windows |
| **Language** | follow the language Windows is showing, or pin Arabic or English |
| **The key bar** | show or hide the row of key buttons under the ring |
| **Your keys** | bind Convert, Tools and Actions to Shift, Ctrl, Alt or Win — independently |
| **The trigger** | the key alone, or also a short rest of the pointer (80–900 ms) |

The settings window previews the ring live: every slider redraws it, in your language and your
direction. Scroll the page and the preview moves out to a floating card in the corner you choose —
drag it to another corner and it stays there — so you can keep watching the ring while editing
rows further down. Nothing jumps back to the top when you change something.

<p>
  <img src="docs/images/en-settings.png" width="400" alt="Settings, English">
  <img src="docs/images/ar-settings.png" width="400" alt="Settings, Arabic">
</p>

<p>
  <img src="docs/images/en-settings-scrolled.png" width="400" alt="Settings scrolled, with the live preview floating in the bottom-right corner">
</p>

Options with only a few choices are a row you click once, not a dropdown you open — shape, density,
names, mode, language. Sliders jump straight to the value you click.

### Presets

| | | | |
|---|---|---|---|
| <img src="docs/images/gallery-windows.png" width="170"><br>**Windows** — your accent and material | <img src="docs/images/gallery-fluent.png" width="170"><br>**Fluent** — soft acrylic card | <img src="docs/images/gallery-paper.png" width="170"><br>**Paper** — flat and light | <img src="docs/images/gallery-graphite.png" width="170"><br>**Graphite** — flat and dark |
| <img src="docs/images/gallery-terminal.png" width="170"><br>**Terminal** — outlined, no fill | <img src="docs/images/gallery-vivid.png" width="170"><br>**Vivid** — saturated shapes | <img src="docs/images/gallery-contrast.png" width="170"><br>**Contrast** — heavy strokes | |

### Share a look

The appearance lives in its own file, `%APPDATA%\DragDone\theme.json`, separate from paths and
machine settings. **Settings → Sharing a look** exports and imports it, and the file is plain JSON
you can edit by hand or commit next to your dotfiles. The schema and every field are documented in
[docs/THEMES.md](docs/THEMES.md).

```jsonc
{
  "ThemePreset": "graphite",
  "Layout": "Orbit",
  "Accent": "#7BD3FF",
  "RotatedLabels": false
}
```

## How it works

1. Start dragging one or more files from the Desktop or File Explorer.
2. While the mouse button is still held, press a key:

   | Key | Menu | What it holds |
   |---|---|---|
   | <kbd>Shift</kbd> | Convert | Target formats for the dragged type |
   | <kbd>Ctrl</kbd> | Tools | Crop, resize, rotate, compress, extract… |
   | <kbd>Alt</kbd> | Actions | Share, move, rename, zip, copy path, terminal, shelf |

   All three are rebindable, and all three are shown as pills under the ring. Press another key
   while the menu is open to switch menus, or hover a tab name.
3. Drop the files onto a sector. Sectors with a sub-ring (crop ratios, rotation, sizes) open it on
   hover; dropping on the parent runs its default (free crop, rotate right, 50%).
4. A progress ring runs in the hub, then a result chip offers **Show in folder** and **Undo**.

The ring opens centred on the pointer, so Explorer's own drag ghost always covers the hub. The
file kind, size and count are therefore shown in a pill **above** the ring, where nothing overlaps
it.

A key that was already held when the drag began is treated as Explorer's own copy/move intent and
does not open the menu; press it again to summon. Move away from the ring or press <kbd>Esc</kbd> to
dismiss it, and the drag continues as a normal Explorer drag. Settings can also open the Convert
menu when the cursor rests for a moment during a drag.

## Actions

| Dragged | Convert (Convert key) | Tools (Tools key) |
|---|---|---|
| Images | PNG, JPG, WebP, PDF, ICO; more: BMP, TIFF, GIF, AVIF | Compress, crop (free, 1:1, 4:3, 16:9, 9:16, auto-trim), rotate/flip, resize (25/50/75%, 1920/1280/800 px), strip metadata, combine into one PDF |
| Video | MP4, MOV, WebM, GIF, MP3, M4A | Compress (light / balanced / strong), extract audio, mute, frame snapshot, rotate, resize (1080p/720p/480p) |
| Audio | MP3, M4A, WAV, FLAC, OGG, Opus | Compress, normalize loudness, trim silence, convert to mono |
| PDF | PNG, JPG (one image per page) | Merge, split into pages, rotate, compress, strip metadata |
| Documents | PDF (plain text built in; Word/Excel/PowerPoint through installed Office) | — |
| Archives | — | Extract here |
| Anything | — | Zip, SHA-256 hash |

**Actions (Actions key)** apply to every type: Windows Share sheet (Nearby Sharing where enabled),
move to Desktop / Documents / Downloads / Pictures / recent folders / a chosen folder, rename, copy
path, open a terminal there, and a floating shelf that holds files until you drag them somewhere
else. The shelf shows its files as a grid of cards — a picture preview (or the file's own icon),
the name, and a corner menu to open it, show it in Explorer, or remove it — and the whole card is
what you drag out.

<p>
  <img src="docs/images/en-shelf.png" width="330" alt="Shelf, English, file cards in a grid">
  <img src="docs/images/ar-shelf.png" width="330" alt="Shelf, Arabic, right-to-left grid">
</p>

The current format is shown dimmed. Actions that need a missing component stay visible and say why
instead of disappearing.

## Rules it keeps

- **Never overwrites.** Outputs get a descriptive suffix (`photo - compressed.png`,
  `photo - مضغوط.png` in Arabic) and `(2)`, `(3)` on collision.
- **Next to the source** unless an output folder is set in Settings.
- **Undo** removes created files or moves files back.
- **Offline.** Nothing is uploaded or downloaded. FFmpeg is used only if it is already on the
  machine.
- **Out of the way.** The overlay never takes focus. Between drags the process trims itself to a few
  megabytes of working set.
- **Readable.** Its two settings files accept comments and trailing commas, and out-of-range values
  are clamped instead of trusted.

## Requirements

- Windows 10 1809 or later, or Windows 11.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (not needed for a
  self-contained publish). Building from source needs the **.NET 10 SDK**.
- **Optional:** `ffmpeg.exe` for video/audio work and WebP/AVIF images. DragDone looks in the
  configured path, next to `DragDone.exe`, on `PATH`, and in common install folders (winget, Scoop,
  Chocolatey).
- **Optional:** Microsoft Office for Word/Excel/PowerPoint → PDF.

Images use Windows Imaging Component, PDF pages render with `Windows.Data.Pdf`, and PDFs are
assembled with [PDFsharp](https://github.com/empira/PDFsharp) 6.2.4 (MIT).

## Build and run

```bash
dotnet build DragDone.sln
dotnet test DragDone.sln
src/DragDone/bin/Debug/net10.0-windows10.0.19041.0/DragDone.exe            # tray
```

Release build. The framework-dependent folder is about 27 MB, of which 24 MB is the Windows Runtime
projection used for PDF rendering and the Share sheet. The self-contained build bundles .NET and WPF
and comes to about 200 MB.

```bash
dotnet publish src/DragDone -c Release -r win-x64 --self-contained false -o artifacts/publish   # needs the .NET 10 Desktop Runtime
dotnet publish src/DragDone -c Release -r win-x64 --self-contained -o artifacts/publish         # runs anywhere
```

Command-line modes, useful for scripting and verification:

| Command | Effect |
|---|---|
| `DragDone.exe` / `--tray` | Run in the tray (a second launch opens Settings in the running instance) |
| `--settings` | Open Settings |
| `--run <actionId> <files…> [--report out.json] [--lang ar\|en]` | Run one action headless, e.g. `--run convert:jpg photo.png` |
| `--snapshot <folder>` | Render the ring in 104 states (presets × layouts × densities × labels, both languages) |
| `--show <window> [ar\|en] [--capture out.png] [--live] [--filter text] [--scroll px]` | Open `settings`, `welcome`, `crop`, `rename` or `shelf` with sample data; `--live` uses your own settings and theme instead, `--filter` narrows the settings window to the matching rows, `--scroll` captures the page further down (where the preview floats). Without a language, the window uses the language the app would use. |
| `--selftest settings\|shelf [--out report.txt]` | Check a window in-process — the settings choice rows, the floating preview appearing and hiding on scroll, the scroll position surviving a change, the corner a drag picks; or the shelf grid, its corner menus and its rebuild on removal. Nothing is shown or clicked on screen. |
| `--lang-info [--out file.txt]` | Report which language the app would use and why — the mode, the system language, the resolved language and the text direction |

## Verification

- `dotnet test` — 101 tests: drag state machine and key bindings, ring geometry per layout, palette
  derivation, settings and theme files (round trip, precedence, clamping, comments), the remembered
  preview corner, catalogue rules, collision-safe paths, payload classification, localisation
  parity, language resolution against the real Windows setting, and the overlay regressions found
  by driving the real app (a finished job must restore the ring, and the window must be sized to
  fit the ring and the bar below it).
- `--selftest settings` — 13 checks on the live Settings window, without a pointer or a screenshot.
- `--selftest shelf` — 7 checks on the live shelf: one card per file, cards wrapping into a grid,
  the corner menu on each, and the grid rebuilding on removal and clearing.
- `bash tools/smoke.sh` — runs 44 real actions on generated media and reports each output.
- `python tools/settings_smoke.py <exe>` — opens Settings and changes the theme and layout the
  way a person would, then fails if the page loses its controls, goes blank, or logs a crash. The
  page is rebuilt whenever the palette changes, so this is the check that catches a rebuild that
  throws half way through.
- `python tools/capture_windows.py <exe> <dir>` — renders every window in both languages without
  touching the screen.
- `python tools/e2e_drag.py --exe <exe> --dir <folder> --out <dir> --key Shift --sector convert:jpg`
  — drives a **real** Explorer drag with injected mouse and keyboard input, drops the file on the
  named sector and reports what was created. The harness never injects input unless it has first
  proved the press point belongs to the Explorer window it opened, and it never releases a file
  outside a DragDone window.

### Tracing a drag

Set `DRAGDONE_TRACE` to a file path before starting the app to record every decision the drag
pipeline makes — press, eligibility, phase change, summon, OLE enter, and each sector's position on
screen. With the variable unset nothing is written and nothing changes.

```
$env:DRAGDONE_TRACE="$env:TEMP\dragdone.log"
DragDone.exe --tray
```

## Layout

```
src/DragDone/
  Core/          payload, file kinds, action catalogue, executor, settings, look, keys, output paths
  Processors/    image (WIC), PDF, media (FFmpeg), documents, files, share
  Shell/         mouse hook, drag state machine, Explorer/Desktop watcher, tray host, startup
  UI/            overlay, radial menu, theme engine, settings, welcome, crop, rename, shelf
  Localization/  ar.json, en.json, Loc
tests/DragDone.Tests/
tools/           icon generator, smoke test, window capture, end-to-end drag harness
docs/            ARCHITECTURE.md, THEMES.md, images
```

## Contributing

Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) for the build, the two
rules that outrank everything, and how to add an action or a preset. The internals are described in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), the appearance model in
[docs/THEMES.md](docs/THEMES.md), the original brief in [docs/IDEA.md](docs/IDEA.md), and the history
in [CHANGELOG.md](CHANGELOG.md).

## Known limits

- Detection works for drags that start in File Explorer or on the Desktop. Drags from other
  applications are ignored by design (the scope can be widened in Settings).
- An app running as administrator does not receive the overlay's drop, because Windows blocks
  drag-and-drop from a normal process into an elevated one.
- One job runs at a time; the ring ignores new drops until the current job finishes.
- Windows 11 adds Mica to DragDone's own windows; on Windows 10 the material is acrylic or opaque,
  depending on the preset.

## License

MIT — see [LICENSE](LICENSE).
