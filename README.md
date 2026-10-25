# DragDone

[![build](https://github.com/trookish/DragDone/actions/workflows/build.yml/badge.svg)](https://github.com/trookish/DragDone/actions/workflows/build.yml)
[![release](https://img.shields.io/github/v/release/trookish/DragDone?color=2ea44f)](https://github.com/trookish/DragDone/releases/latest)
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

Three of the nine shapes it can take, from the same menu: a donut ring, an orbit of badges around an
open centre, and a solid wheel. Labels, density, colours, material, size and motion are all yours to
set, and you can add your own programs, conversions and file types to the rings.

## Make it yours

Nothing about the look is hard-coded. Open **Settings** from the tray icon:

| | |
|---|---|
| **Character** | seven presets — Windows, Fluent, Paper, Graphite, Terminal, Vivid, Contrast — then adjust anything |
| **Shape** | Donut, solid wheel, orbit, petals, honeycomb, tiles, diamonds, fan, or automatic; diameter, gap, outline thickness |
| **Motion** | nine opening animations (or none) and nine hover effects, with their length, strength and speed; follows the Windows animation setting unless you turn that off |
| **Density** | compact, roomy, large — spacing and type size together |
| **Names** | automatic, symbols, symbols and names, or text only; rotate outside labels or keep them upright |
| **Colour** | follow the Windows accent, pick a named colour, or choose any colour; accent strength and contrast |
| **Material** | translucent surface, backdrop blur, light / dark / follow Windows |
| **Language** | follow the language Windows is showing, or pin Arabic or English |
| **The key bar** | show or hide the row of key buttons under the ring |
| **Your keys** | bind Convert, Tools and Actions to Shift, Ctrl, Alt or Win — independently |
| **The trigger** | the key alone, or also a short rest of the pointer (80–900 ms) |
| **Custom actions** | run any program on the dropped files, or make it a conversion with its own output format |
| **File types** | tell DragDone which kind an unknown extension is, so those files get the right rings |

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

Options with only a few choices are a row you click once — density, names, mode. Longer lists such
as shape, animation, hover effect, language and colour are dropdowns. Sliders jump straight to the
value you click.

### Shapes and motion

Every shape draws the same actions, so switching is purely a matter of taste:

<p>
  <img src="docs/images/shapes.png" width="820" alt="The eight drawn shapes: donut, solid wheel, petals, fan, orbit, honeycomb, tiles and diamonds">
</p>

The ring can open with **Fade**, **Pop**, **Zoom**, **Spin**, **Cascade**, **Bloom**, **Drop**,
**Elastic** or **Ripple**, or with no animation at all. The same motion, shortened, plays when you
switch menus or open a sub-ring. Each column below is one animation at 15%, 40% and 70% of the way
through:

<p>
  <img src="docs/images/animations.png" width="820" alt="Opening animations, three frames each">
</p>

While a drag rests on a sector it can **Grow**, **Lift**, **Glow**, **Highlight**, **Outline**,
**Pulse**, **Tint**, **Magnify** or **Spotlight**. Strength and transition speed are separate sliders,
and every change replays in the preview:

<p>
  <img src="docs/images/hover.png" width="820" alt="Hover effects on the ring and on badge shapes">
</p>

<p>
  <img src="docs/images/en-settings-motion.png" width="460" alt="The Motion settings with the floating live preview">
</p>

When **Animation effects** is off in Windows accessibility settings, DragDone turns its motion off as
well, unless you tell it not to.

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

<p>
  <img src="docs/images/en-multi.png" width="520" alt="A batch of files, counted in a pill above the Tools ring">
</p>

A key that was already held when the drag began is treated as Explorer's own copy/move intent and
does not open the menu; press it again to summon. Move away from the ring or press <kbd>Esc</kbd> to
dismiss it, and the drag continues as a normal Explorer drag. Settings can also open the Convert
menu when the cursor rests for a moment during a drag.

## Actions

The same three rings for images, video, audio, PDFs, documents and archives — the Tools ring changes
with what you are dragging:

<p>
  <img src="docs/images/en-tools.png" width="400" alt="Tools ring for a batch of files, English">
  <img src="docs/images/ar-tools.png" width="400" alt="Tools ring, Arabic, right-to-left">
</p>

| Dragged | Convert (Convert key) | Tools (Tools key) |
|---|---|---|
| Images | PNG, JPG, WebP, PDF, ICO; more: BMP, TIFF, GIF, AVIF | Compress, crop (free, 1:1, 4:3, 16:9, 9:16, auto-trim), rotate/flip, resize (25/50/75%, 1920/1280/800 px), strip metadata, combine into one PDF |
| Video | MP4, MOV, WebM, GIF, MP3, M4A | Compress (light / balanced / strong), extract audio, mute, frame snapshot, rotate, resize (1080p/720p/480p) |
| Audio | MP3, M4A, WAV, FLAC, OGG, Opus | Compress, normalize loudness, trim silence, convert to mono |
| PDF | PNG, JPG (one image per page) | Merge, split into pages, rotate, compress, strip metadata |
| Word documents (DOCX, DOC, ODT, RTF) | PDF, DOCX, DOC, ODT, RTF, TXT, HTML — through installed Word | — |
| Spreadsheets (XLSX, XLS, ODS, CSV) | PDF, XLSX, XLS, ODS, CSV — through installed Excel | — |
| Presentations (PPTX, PPT, ODP) | PDF, PPTX, PPT, ODP — through installed PowerPoint | — |
| Text and code (TXT, MD, CSV, JSON, XML, LOG…) | PDF, built in | — |
| Archives | — | Extract here |
| Anything | — | Zip, SHA-256 hash |

Crop and rename open a window of their own rather than a dialog:

<p>
  <img src="docs/images/en-crop.png" width="330" alt="Crop window">
  <img src="docs/images/en-rename.png" width="330" alt="Rename window">
</p>

**Actions (Actions key)** apply to every type: Windows Share sheet (Nearby Sharing where enabled),
move to Desktop / Documents / Downloads / Pictures / recent folders / a chosen folder, rename, copy
path, open a terminal there, and a floating shelf that holds files until you drag them somewhere
else. The shelf shows its files as a grid of cards — a picture preview (or the file's own icon),
the name, and a corner menu to open it, show it in Explorer, or remove it — and the whole card is
what you drag out.

<p>
  <img src="docs/images/en-actions.png" width="520" alt="The Actions ring: share, move, rename, terminal, shelf">
</p>

<p>
  <img src="docs/images/en-shelf.png" width="330" alt="Shelf, English, file cards in a grid">
  <img src="docs/images/ar-shelf.png" width="330" alt="Shelf, Arabic, right-to-left grid">
</p>

Every job ends in a result chip with **Undo**, **Open** and **Show in Explorer** — the files sit
next to the original, never on top of it:

<p>
  <img src="docs/images/en-result.png" width="400" alt="Result chip, English">
  <img src="docs/images/ar-result.png" width="400" alt="Result chip, Arabic">
</p>

The current format is shown dimmed. Actions that need a missing component stay visible and say why
instead of disappearing.

### Your own actions

**Settings → Custom actions** adds any program to a ring. Give it a name, the file types it applies
to (`.png, .jpg`, a kind such as `image` or `video`, or `*`), the program, its arguments, and which
ring it belongs in. The placeholders `{file}`, `{files}`, `{dir}`, `{name}` and `{ext}` are filled in
for each drop and paths are quoted for you.

Set an **output format** and the action becomes a conversion: DragDone chooses `{out}` beside the
original — `photo.jxl`, or `photo (2).jxl` if that exists — so a custom tool can never overwrite a
file either. A failed run reports the program's exit code and leaves nothing behind.

```text
Name       JPEG XL
Types      image
Program    magick
Arguments  {file} {out}
Output     jxl
```

<p>
  <img src="docs/images/en-settings-custom.png" width="400" alt="Custom actions, English">
  <img src="docs/images/ar-settings-custom.png" width="400" alt="Custom actions, Arabic">
</p>

Custom actions join the ring directly while there is room. Beyond that they share one **Custom**
sector with a sub-ring, so the ring never overflows. **Settings → File types** maps an extension
DragDone does not know — `.cr3`, `.mts`, `.jxl` — to image, video, audio, document or archive, and
those files then get that kind's rings and your custom actions.

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

## Get it

Download **[DragDone setup](https://github.com/trookish/DragDone/releases/latest)** and run it.
The installer carries everything the app needs — including the .NET runtime — so there is nothing
else to download first.

- Installs for the current user, so **no administrator prompt**. Choose an all-users install in the
  wizard if you would rather.
- Lands in `%LOCALAPPDATA%\Programs\DragDone`, adds a Start Menu shortcut (a desktop one is
  optional), and appears in **Apps & features** for a clean uninstall.
- Uninstalling stops the tray app, removes its startup entry, and asks before deleting your
  settings.
- The wizard is translated: English and العربية.

Prefer not to install? A self-contained publish is a single folder you can run from anywhere — see
[Build and run](#build-and-run).

A **Microsoft Store package** is also built from the same source. It is the same tray app, signed by
Microsoft instead of by nobody, and it updates itself through the Store. Submitting it needs a
Partner Center identity — see [docs/STORE.md](docs/STORE.md) for the package, the verification script,
and the listing text.

DragDone does not collect anything or talk to the network: [PRIVACY.md](PRIVACY.md).

The first run opens a card with the three keys and where to change them:

<p>
  <img src="docs/images/en-welcome.png" width="400" alt="First run, English">
  <img src="docs/images/ar-welcome.png" width="400" alt="First run, Arabic">
</p>

## Requirements

- Windows 10 1809 or later, or Windows 11.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (not needed for a
  self-contained publish). Building from source needs the **.NET 10 SDK**.
- **Optional:** `ffmpeg.exe` for video/audio work and WebP/AVIF images. DragDone looks in the
  configured path, next to `DragDone.exe`, on `PATH`, and in common install folders (winget, Scoop,
  Chocolatey).
- **Optional:** Microsoft Office for Word, Excel and PowerPoint conversions. DragDone drives the
  installed applications locally and never uploads anything. Excel needs a printer driver such as
  Microsoft Print to PDF to produce a PDF.

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

The installer. It publishes the self-contained build and compiles `installer/DragDone.iss` into one
setup file; the version comes from the csproj.

```bash
pwsh tools/build_installer.ps1            # artifacts\DragDone-<version>-setup.exe
```

Needs [Inno Setup 6](https://jrsoftware.org/isdl.php) (`winget install JRSoftware.InnoSetup`). Tagging
a release builds the same file in CI and attaches it to the release.

Command-line modes, useful for scripting and verification:

| Command | Effect |
|---|---|
| `DragDone.exe` / `--tray` | Run in the tray (a second launch opens Settings in the running instance) |
| `--settings` | Open Settings |
| `--run <actionId> <files…> [--report out.json] [--lang ar\|en]` | Run one action headless, e.g. `--run convert:jpg photo.png` |
| `--snapshot <folder>` | Render the ring in 138 states: every preset and shape, densities, labels, hover effects and frames of each opening animation |
| `--show <window> [ar\|en] [--capture out.png] [--live] [--filter text] [--scroll px]` | Open `settings`, `welcome`, `crop`, `rename` or `shelf` with sample data; `--live` uses your own settings and theme instead, `--filter` narrows the settings window to the matching rows, `--scroll` captures the page further down (where the preview floats), `--examples` fills in sample custom actions and file types. Without a language, the window uses the language the app would use. |
| `--selftest settings\|shelf [--out report.txt]` | Check a window in-process — the settings choice rows, the floating preview appearing and hiding on scroll, the scroll position surviving a change, the corner a drag picks; or the shelf grid, its corner menus and its rebuild on removal. Nothing is shown or clicked on screen. |
| `--lang-info [--out file.txt]` | Report which language the app would use and why — the mode, the system language, the resolved language and the text direction |

## Verification

- `dotnet test` — 180 tests: drag state machine and key bindings, ring geometry per layout, palette
  derivation, settings and theme files (round trip, precedence, clamping, comments), the remembered
  preview corner, catalogue rules, collision-safe paths, payload classification, localisation
  parity, language resolution against the real Windows setting, and the overlay regressions found
  by driving the real app (a finished job must restore the ring, and the window must be sized to
  fit the ring and the bar below it). Also every shape, animation and hover effect, the Office
  format targets, and custom actions run end to end through a real process.
- `--selftest settings` — 21 checks on the live Settings window, without a pointer or a screenshot.
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
installer/       Inno Setup script for the release installer
tools/           icon generator, smoke test, window capture, installer build, end-to-end drag harness
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
- Office conversions need the matching Office application installed; OpenDocument and CSV output
  follow what that application can save.
- Windows 11 adds Mica to DragDone's own windows; on Windows 10 the material is acrylic or opaque,
  depending on the preset.

## License

MIT — see [LICENSE](LICENSE).
