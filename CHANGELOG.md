# Changelog

All notable changes to DragDone. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [semantic versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.0] — 2026-10-25

### Added

- **Five new shapes.** Petals, Honeycomb, Tiles, Diamonds and Fan join Donut, solid wheel, Orbit
  and Automatic. The shape is now chosen from a dropdown.
- **Motion.** Nine opening animations (Fade, Pop, Zoom, Spin, Cascade, Bloom, Drop, Elastic,
  Ripple, or none) with an adjustable length, and nine hover effects (Grow, Lift, Glow, Highlight, Outline,
  Pulse, Tint, Magnify, Spotlight) with their own strength and transition speed. A shortened
  animation plays when switching menus or opening a sub-ring. Motion turns off with the Windows
  **Animation effects** setting unless you choose otherwise. The live preview replays every change.
- **Office conversions.** Word documents convert to PDF, DOCX, DOC, ODT, RTF, TXT and HTML;
  spreadsheets to PDF, XLSX, XLS, ODS and CSV; presentations to PDF, PPTX, PPT and ODP — all through
  the Office applications already installed, offline.
- **Custom actions.** Settings → Custom actions adds any program to a ring, with file-type matching,
  argument placeholders (`{file}`, `{files}`, `{dir}`, `{name}`, `{ext}`, `{out}`) and a choice of
  ring. Setting an output format makes it a conversion whose output DragDone names beside the
  original, so it never overwrites a file. Extra actions share a **Custom** sub-ring when the ring is
  full.
- **File types.** Settings → File types assigns an unknown extension to image, video, audio,
  document or archive, so those files get the right rings.
- `--show … --examples` fills the customisation cards with samples for screenshots.

### Changed

- The installed size is smaller: WinForms is gone, and only the English and Arabic framework
  resources ship.
- Filtering Settings hides sections whose rows were all filtered out, instead of leaving empty
  cards.

### Fixed

- Excel → PDF on a machine without a printer driver now explains that Excel needs one, instead of
  reporting a generic Office failure.

## [1.1.1] — 2026-10-21

### Changed

- The package is built with the real Partner Center identity (`trookish.DragDone`,
  `CN=9121850F-2302-4F5F-8644-8CDC73A97BA8`), so the artifact attaches to a release ready to upload
  to the Store. The verification scripts read the identity from `packaging/identity.json` instead of
  a hard-coded development name.
- Verified on the packaged app: installs as `trookish.DragDone_1.1.1.0_x64__zr0xas6pssvja`, reports
  `packaged = True` and `startup = startupTask=Enabled`, runs an FFmpeg job, and uninstalls clean.

## [1.1.0] — 2026-10-21

### Added

- **A Microsoft Store package.** `tools/build_msix.ps1` publishes the same app self-contained and
  packs it as an MSIX with `MakeAppx`, and CI attaches `DragDone-<version>-x64.msix` to every release
  next to the installer. The package is unsigned on purpose: Partner Center signs it during
  publishing, which is how Store users avoid the SmartScreen prompt the GitHub installer shows.
- **Packaged startup.** A Store build cannot rely on `HKCU\...\Run`, so it registers a Windows
  startup task instead and reports its state through `--identity`. The installer build keeps using
  the Run key.
- **`--identity`** prints packaged status, package name and family, version, install folder, startup
  mode, settings folder and executable path — the fastest way to see which channel is running.
- **`packaging/verify_msix.ps1`** and the elevated variant install a package end to end, check
  identity, the startup task, redirected settings and a real FFmpeg job, then uninstall and remove
  the development certificate again.
- [docs/STORE.md](docs/STORE.md) covers submission, and [PRIVACY.md](PRIVACY.md) states plainly that
  nothing leaves the machine.

### Changed

- Startup registration checks for package identity before touching the registry.

## [1.0.2] — 2026-10-21

### Fixed

- Uninstalling no longer has any way to delete your settings and theme without being asked. In
  unattended mode Inno Setup's plain `MsgBox` answers "Yes" to its own question, so a silent
  uninstall used to clear `%APPDATA%\DragDone` — which is also what `winget uninstall` would have
  done. The question now uses `SuppressibleMsgBox` with "No" as its unattended answer, so settings
  survive unless someone clicks Yes.

## [1.0.1] — 2026-10-21

### Fixed

- Video and audio jobs no longer fail with `An error occurred trying to start process … ffmpeg.exe`.
  Setup used to launch the app from its own temp folder, which is deleted when Setup closes, and
  every child process inherited that dead working directory. The installer now starts the app in
  its install folder, the app moves itself out of a working directory that no longer exists, and
  FFmpeg is always launched from a folder that is really there.
- FFmpeg is now verified by running it, not by checking that the file exists. A WinGet or Scoop
  shim left behind by an uninstall is skipped instead of being handed a job it cannot do, and a
  launch that still fails says "Requires FFmpeg. Set its location in Settings." rather than
  dumping a Win32 message.
- Audio actions on a clip with no audio track, and video actions on a file with no video track,
  now say exactly that instead of showing FFmpeg's `Stream map '' matches no streams`.
- The live preview is a square plate with the ring drawn tight to the bounds it actually occupies
  and centred to the pixel, so it no longer sits small and off-centre in a wide box or loses its
  outer labels at large ring sizes. The hint is centred under it, and the floating copy is opaque
  so the rows behind it no longer ghost through.
- The app icon in Settings is drawn from the largest frame in the file instead of the 16px one
  stretched to 40px, which is what made it look soft.
- Changing a value no longer loses the scroll position when the change rebuilds the page and
  repaints the window in the same instant; the window chrome is only re-templated when the chrome
  itself changes.

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
