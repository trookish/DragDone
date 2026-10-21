# Architecture

DragDone is one tray process. There is no main window: the application is a message loop, a
mouse hook, and five windows that exist only while they are needed.

```
App.xaml.cs            single instance, command line, lifetime
  └─ Shell/AppHost     tray icon, settings, jobs, undo, idle trim
       ├─ Shell/DragWatcher     low-level hook → state machine → overlay
       ├─ UI/OverlayWindow      the ring, its tabs, its result state
       └─ UI/*Window            settings, welcome, crop, rename, shelf
```

## The drag pipeline

1. **Hook.** `Shell/MouseHook` installs a `WH_MOUSE_LL` hook. It sees button and move events
   before any window does, which is the only way to notice a shell drag while it is happening.
2. **Filter.** `DragWatcher` ignores presses that did not land on the Desktop or File Explorer
   (unless the scope is widened in Settings), and ignores presses while a job is running.
3. **State machine.** `DragStateMachine` is pure: it takes points, key state and time, and returns
   a `MenuTab` to open or nothing. It enforces the drag threshold, remembers which keys were
   already down when the drag began (so <kbd>Ctrl</kbd>-drag to copy is not mistaken for a summon),
   and honours the optional "rest to open" trigger.
4. **Overlay.** `OverlayWindow` shows without activating (`WS_EX_NOACTIVATE`), stays topmost, and
   registers itself as an OLE drop target. The payload is read from the `IDataObject` only when
   Windows offers it, which means a text selection never produces a file ring.
5. **Run.** `ActionExecutor` dispatches the chosen action id to a processor on a worker thread and
   reports a `JobResult`; the overlay shows progress, then the result with Undo, Open and Reveal.

`Shell/DragStateMachine` and `Core/OutputPaths` are the two places where most of the product's
rules live, and both are covered directly by tests.

## The ring

`UI/RadialMenu` is a single `FrameworkElement` that draws everything in `OnRender`: sectors, hub,
labels, spinner. It is always laid out left-to-right, because the ring is geometry, not text flow;
Arabic labels still shape and order correctly inside their own `FormattedText`.

`RadialMenu.Measure(Settings, sectorCount)` is pure and returns a `MenuGeometry`:

| Layout | Shape | Labels |
|---|---|---|
| `Donut` | thin ring with a wide hub | outside, on surface pills |
| `Pie` | filled wedges from the hub | inside the wedge |
| `Orbit` | floating badges around an open centre | inside the badge |
| `Auto` | 4 or fewer sectors: `Orbit`; 7 or fewer: `Donut`; otherwise `Pie` | follows the chosen layout |

Labels outside the ring are placed by their own bounding half-extent, so a wide label can never
creep back over the rim at any angle. They sit on a small surface pill: the ring floats over an
unknown desktop, and bare text would disappear on some wallpapers. The hovered pill is filled with
the accent colour.

Hit testing (`HitTestRing`) mirrors the same geometry: a hub zone, a ring zone divided by angle,
and, for `Orbit`, forgiving square targets around each badge.

The overlay window is sized from the ring, the tab bar and the caption, and the ring is placed so
its centre lands exactly on the pointer. Explorer's own drag ghost is therefore always drawn over
the hub, which is why the file kind, size and count live in a pill above the ring instead of in
the hub itself.

### The animation trap

`RadialMenu.Collapse` is a dependency property with a `HoldEnd` animation while a job runs. A
`HoldEnd` animation keeps winning over the local value forever, so setting `Collapse = 0` again
does nothing. `ShowAt` clears the animation before resetting the value; without that, the ring
stays invisible for the rest of the session and only the hub is drawn.

## The theme

`UI/Theme` derives one `Palette` — surfaces, text, stroke, accent, status colours — from five
inputs: preset, mode, accent colour, accent strength and contrast. Nothing else in the UI hard-codes
a colour; every surface asks `Theme.Current`.

```
Settings ──▶ PaletteFor ──▶ Palette record ──▶ Theme.Brush / Theme.On
    ▲                                              │
    └──── Theme.Changed (palette or look changed) ◀─┘
```

`Theme.Apply()` compares a *look print* (preset, mode, accent, layout, density, labels, geometry)
rather than only the palette, because layout and density change without touching a colour. When it
differs, `Theme.Changed` fires: the overlay re-lays itself out, `FluentWindow` re-applies the title
bar and window material, and the settings window redraws its preview.

## Settings on disk

Two files, both indented JSON that accept comments and trailing commas:

| File | Holds |
|---|---|
| `%APPDATA%\DragDone\settings.json` | language, key bindings, triggers, paths, quality, startup |
| `%APPDATA%\DragDone\theme.json` | everything about the look, and nothing about this machine |

`theme.json` wins when both describe the look, so a theme can be dropped in by hand. `Core/Look`
is that contract: `Look.From(settings)`, `look.CopyTo(settings)`, and the same shape used by
export and import.

`Settings.Normalized()` runs on every load: it clamps every numeric range, rejects an invalid hex
colour, sanitises key names against `Core/Keys.All`, and drops folders that no longer exist. A
hand-edited file cannot put the app into an unusable state.

## Two channels, one binary

The installer build and the Store build differ in exactly one place: how the app is allowed to start
with Windows.

| | Installer (GitHub) | Store (MSIX) |
|---|---|---|
| Identity | none | `Windows.ApplicationModel.Package.Current` answers |
| Startup | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | a declared startup task, `TaskId = "DragDone"` |
| Signing | none | Microsoft, at publishing time |
| Updates | new release, reinstall | the Store |

`Shell/PackageInfo` asks for the package once and caches the answer, treating the exceptions an
unpackaged process can throw — `InvalidOperationException` from the projection, and
`TypeLoadException`, `COMException` or `IOException` when it cannot resolve at all — as "no
identity". `Shell/Startup` then either writes the Run key or asks the startup task to enable itself.
`--identity` prints the result so a packaged run can be checked without reading logs.

A packaged process also gets its `%APPDATA%` writes redirected into the package by Windows when the
package declares that behaviour. The app asks for the folder the same way in both channels and never
assumes which physical path it lands on, so a package that redirects and one that does not both work.

## Language

`Localization/Loc` holds the catalogue and the current language. `Loc.IsRtl` decides the text
direction, and every window takes its direction from `Loc.Flow`, so Arabic and English are the
same code path with different strings.

The language comes from three places, in this order:

1. **The user's choice.** `Settings.LanguageMode` is `System`, `Arabic` or `English`. `System` is
   the default, so a fresh install already matches the machine.
2. **Windows' display language.** `Loc.SystemLanguage()` resolves it from three sources, most
   reliable first:
   - `HKCU\Control Panel\Desktop\PreferredUILanguages` — the list the user edits in Windows' own
     language settings. This is the one that changes the moment they change it.
   - `GetUserDefaultUILanguage()` — always correct, but Windows only fixes it at sign-in.
   - `CultureInfo.CurrentUICulture` — the thread's culture, as a last resort.

   The list is walked in order and the first language DragDone ships wins, so a machine set to
   French with Arabic second still lands on Arabic.
3. **A fallback.** Arabic, when Windows is in a language DragDone does not ship.

Windows does raise a preference change when the language changes, but it reports the change under
a category that does not identify the language, so the event alone cannot be trusted. `AppHost`
re-checks every ten seconds as well, which is what makes the running app follow a language change
without being restarted. `Loc.LanguageChanged` then rebuilds the tray menu, flips the direction of
every open window, and re-lays the overlay.

A settings file written before this behaviour existed carries a plain `"Language"` code and no
mode. English there was a deliberate choice and is honoured; Arabic was only the old default, so
it must not pin the language on a machine that Windows shows in another language.

## Jobs and undo

`Core/Jobs` records what a job created or moved. Undo deletes created files and moves moved files
back; it is offered from the result chip and from the tray menu, and it is cleared once the process
restarts. `Core/OutputPaths` is the only place that names an output: it never overwrites, and adds
a descriptive suffix plus `(2)`, `(3)` on collision.

## Verification

| Command | Covers |
|---|---|
| `dotnet test` | state machine, geometry, catalogue, paths, payloads, settings and themes, preview corner, localisation parity, language resolution |
| `bash tools/smoke.sh` | 44 real actions on generated media, each output checked |
| `--snapshot <folder>` | renders the ring in 104 states (presets × layouts × densities × labels, both languages) |
| `--selftest settings\|shelf [--out file]` | in-process checks on Settings (choice rows, the floating preview appearing and hiding, the scroll position surviving a change, the corner a drop picks) and on the shelf (one card per file, the grid wrapping, corner menus, rebuild on removal) |
| `--lang-info` | reports which language the app would use, and which source decided it |
| `python tools/settings_smoke.py <exe>` | opens Settings, changes theme and layout, fails on a lost or blank page or a logged crash |
| `python tools/capture_windows.py` | renders every window in both languages off-screen |

## Settings window notes

- **The page is rebuilt, never patched.** A look change throws the page away and builds a
  replacement off-tree, adopting it only once it is complete; a failure leaves the old page up.
  Nothing inside the page is carried across builds — the preview images share a bitmap, not a
  parent.
- **The scroll position is a parked value, not a live one.** `ThemeMode` re-templates the window
  and resets any scroll offset to zero *before* the palette callback runs, so `Rebuild` reads the
  position parked by `OnThemeChanging` and scroll tracking is suspended until the restore has
  settled. Reading the live offset is what made a theme change jump to the top.
- **Few options are a row, many are a list.** Up to four options render as a `Segment` radio row;
  more than that stay a dropdown.
- **The floating preview is a second image, not a moved one.** It appears once the inline preview
  has scrolled out of the viewport, docks to `Settings.PreviewDock`, and its layer stays
  left-to-right so a named corner is the same corner on screen in Arabic and in English.
