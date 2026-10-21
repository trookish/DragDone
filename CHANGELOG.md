# Changelog

All notable changes to DragDone. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [semantic versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] — 2026-10-21

First public release.

### Added

- **A one-file installer.** `DragDone-1.0.0-setup.exe` installs the app and the .NET runtime it needs
  in one step, per user and without an administrator prompt, or for all users if you prefer. It adds
  a Start Menu shortcut, registers in Apps & features, and its uninstall stops the tray app, removes
  the startup entry and asks before touching your settings. The wizard ships in English and Arabic.
- **Radial menu on a drag.** Hold a file drag on the Desktop or in File Explorer, press the Convert,
  Tools or Actions key, and a menu opens under the cursor with the actions for those files. Releasing
  over an action runs it.
- **Safe output.** Results are written next to the original with a suffix, never over the source, and
  `(2)`, `(3)`… are appended on collisions. Every result offers **Undo**, **Open** and **Show in
  Explorer**.
- **Conversions.** Images between PNG, JPG, WebP, BMP, GIF, TIFF, ICO and HEIC; images to PDF; PDF
  pages to PNG or JPG; video and audio through a locally installed FFmpeg. All offline, WIC for
  images, `Windows.Data.Pdf` for PDF rasterising.
- **File actions.** Windows Share sheet, move to Desktop / Documents / Downloads / Pictures / a recent
  folder / a chosen folder, rename, copy path, open a terminal in the folder, compress, and a
  **shelf** that holds files until you drag them out.
- **The shelf** shows its files as a grid of cards — picture preview or the file's own icon, its name,
  and a corner menu to open, reveal or remove it. The whole card is the drag handle.
- **Settings that are actually yours.** Seven appearance presets (Windows, Fluent, Paper, Graphite,
  Terminal, Vivid, Contrast), four ring shapes (donut, solid wheel, orbit, automatic), three
  densities, label styles, any accent colour, translucent surface and blur, ring size, gaps, outline
  weight, hover growth and the key bar.
- **Live preview.** The settings window draws the real ring, so every slider updates it. Scrolling
  past it floats the preview into a corner; drag it to another corner and the choice is remembered.
- **Two settings files.** `%APPDATA%\DragDone\settings.json` holds this machine's paths and keys, and
  `theme.json` holds the look alone so it can be exported, shared and imported.
- **Follows Windows.** Arabic (right-to-left) and English ship; the default follows the Windows
  display language and switches while the app is running. Arabic or English can be pinned instead.
- **Headless modes** for verification and scripting: `--run`, `--snapshot`, `--show`, `--selftest`,
  `--lang-info`.

### Notes

- Windows 10 1809 (17763) or newer, x64. Microsoft Office is only used when it is installed, for the
  Word and Excel conversions.
- FFmpeg is not bundled and is never downloaded: the audio and video actions say what is missing
  instead of failing silently.
- 101 unit tests, 44 real-media smoke actions, 104 rendered overlay states, and in-process checks of
  both the settings window and the shelf.
