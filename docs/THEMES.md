# Themes

The look of DragDone lives in `%APPDATA%\DragDone\theme.json`. It is small, readable, and separate
from machine settings, so one file is a complete look you can copy, commit, or hand to someone else.

Open **Settings → Sharing a look → Export theme** to write one, and **Import theme** to apply
someone else's.

## The file

Every field is optional. Anything missing keeps its default, so a minimal theme is three lines.

```jsonc
{
  "Name": "My night ring",          // label only; ignored on import
  "ThemePreset": "graphite",        // windows fluent paper graphite terminal vivid contrast
  "Theme": "Dark",                  // System | Light | Dark
  "Accent": "teal",                 // system teal blue violet pink amber lime rose
  "AccentHex": null,                // "#RRGGBB" wins over Accent when set
  "AccentStrength": 1.0,            // 0.2 – 1.6   how saturated the accent becomes
  "Layout": "Donut",                // Auto | Donut | Pie | Orbit
  "Density": "Roomy",               // Compact | Roomy | Large
  "MenuDiameter": 352,              // 240 – 520   full ring diameter in pixels
  "Gap": 4,                         // 0 – 16      gap between sectors
  "FrameThickness": 1.2,            // 0 – 4       outline around each sector
  "CornerRatio": 0.10,              // 0 – 0.5     reserved for rounded surfaces
  "HoverGrow": 1.0,                 // 0 – 2       how much a sector grows under the pointer
  "Contrast": 1.0,                  // 0.6 – 1.6   text and edge contrast
  "Translucent": true,              // let the desktop show through
  "BackdropBlur": true,             // soft shadow and blur behind the ring
  "Labels": "Auto",                 // Auto | Icons | Labels | Text
  "RotatedLabels": false,           // turn outside labels with their sector
  "ShowTabBar": true,               // the row of key buttons under the ring
  "ShowHints": true                 // the caption under the ring
}
```

Values outside a range are clamped on load rather than rejected; an invalid `AccentHex` is ignored.

## Presets

A preset is a starting point, not a mode. Pick one, then change anything.

| Preset | Character |
|---|---|
| `windows` | borrows the Windows personalisation accent and window material |
| `fluent` | soft acrylic card, gentle edges, a little more air |
| `paper` | flat and light, no transparency |
| `graphite` | flat and dark, thin outlines, no fill tint |
| `terminal` | outlined ring, no fill, labels along the arc |
| `vivid` | saturated, rounded, playful |
| `contrast` | heavy strokes and maximum text contrast |
| `Auto` layout | 4 or fewer sectors: `Orbit`; 7 or fewer: `Donut`; otherwise `Pie` |

`Theme` and the preset are independent: `paper` in dark mode is a flat dark surface with the same
geometry. `System` follows the Windows light/dark setting and changes live while DragDone sits in
the tray.

## Sharing

Commit a theme next to your dotfiles, or paste it in an issue. To make one the default for a new
machine, copy it to `%APPDATA%\DragDone\theme.json`; it is read on start and wins over the copy of
the look inside `settings.json`.

## Adding a preset in code

1. Add a `Preset` to `Theme.Presets` in `src/DragDone/UI/Theme.cs` — id, intended mode, tint
   (blend fraction of the accent into the surface, 0 – 1), strength multiplier, translucency,
   frame, corner ratio, gap, hover growth.
2. Add `theme.<id>` and `theme.<id>.desc` to both `Localization/ar.json` and `Localization/en.json`.
   `CustomizationTests.Every_preset_is_reachable_by_id_and_has_copy` fails until you do.
3. Run `dotnet test`, then `DragDone.exe --snapshot artifacts/snap` to see it in every layout.
