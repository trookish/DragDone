using System.IO;
using System.Text.Json;
using DragDone.Core;
using DragDone.Localization;
using DragDone.Shell;
using DragDone.UI;
using Xunit;

namespace DragDone.Tests;

/// <summary>
/// The customisation surface: keys, themes, appearance files and the ring layout rules.
/// These are the promises the settings window makes, so they are tested without a window.
/// </summary>
public sealed class CustomizationTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "DragDoneTests", Guid.NewGuid().ToString("N"));
    readonly string _original;

    public CustomizationTests()
    {
        _original = Settings.Folder;
        Settings.Folder = _folder;
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Settings.Folder = _original;
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ keys

    [Fact]
    public void The_floating_preview_docks_bottom_right_by_default()
    {
        var s = new Settings();
        Assert.Equal(PreviewCorner.BottomRight, s.PreviewDock);
    }

    [Fact]
    public void The_chosen_preview_corner_survives_a_save_and_load()
    {
        var s = new Settings { PreviewDock = PreviewCorner.TopLeft };
        s.Save();
        Assert.Equal(PreviewCorner.TopLeft, Settings.Load().PreviewDock);
    }

    [Fact]
    public void An_unknown_preview_corner_falls_back_instead_of_throwing()
    {
        var s = new Settings();
        s.Save();
        var path = Path.Combine(Settings.Folder, "settings.json");
        var json = File.ReadAllText(path).Replace("\"PreviewDock\": \"BottomRight\"", "\"PreviewDock\": 99");
        File.WriteAllText(path, json);
        Assert.Equal(PreviewCorner.BottomRight, Settings.Load().PreviewDock);
    }

    [Fact]
    public void Default_keys_are_three_distinct_keys()
    {
        var s = new Settings();
        Assert.Equal(("Shift", "Ctrl", "Alt"), (s.ConvertKey, s.ToolsKey, s.ActionsKey));
        Assert.Equal(3, new[] { s.ConvertKey, s.ToolsKey, s.ActionsKey }.Distinct().Count());
    }

    [Fact]
    public void Every_key_has_a_label_in_both_languages()
    {
        foreach (var lang in new[] { "ar", "en" })
        {
            Loc.Set(lang);
            foreach (var key in Keys.All)
            {
                var label = Keys.Label(key);
                Assert.False(string.IsNullOrWhiteSpace(label), $"{lang}/{key}");
                Assert.DoesNotContain(".", label); // a raw catalogue key leaked through
            }
        }
        Loc.Set("ar");
    }

    [Theory]
    [InlineData("shift", "Shift")]
    [InlineData("  CTRL ", "Ctrl")]
    [InlineData("Mouse", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Keys_are_sanitised_against_the_supported_set(string? given, string expected) =>
        Assert.Equal(expected, Keys.Sanitize(given));

    [Fact]
    public void Bound_keys_round_trip_per_tab()
    {
        var s = new Settings();
        BoundKeys.Set(s, MenuTab.Convert, "Win");
        BoundKeys.Set(s, MenuTab.Actions, "Alt");
        Assert.Equal("Win", BoundKeys.TabKey(s, MenuTab.Convert));
        Assert.Equal("Alt", BoundKeys.TabKey(s, MenuTab.Actions));
        Assert.Equal("Ctrl", BoundKeys.TabKey(s, MenuTab.Tools));
    }

    [Theory]
    [InlineData("Middle")]
    [InlineData("Right")]
    [InlineData("middle")]
    public void Mouse_buttons_are_rejected_as_triggers(string given)
    {
        // Windows cancels a file drag the moment a mouse button is pressed, so Explorer never
        // hands over the dragged files. A mouse button could open the menu but never act on it.
        Assert.Equal(string.Empty, Keys.Sanitize(given));
        Assert.DoesNotContain(given, Keys.All);
    }

    [Fact]
    public void A_saved_mouse_binding_falls_back_to_the_default_key()
    {
        // A mouse button cannot open a menu, and leaving the tab unbound would make it
        // unreachable, so the default key is restored instead.
        Directory.CreateDirectory(Settings.Folder);
        File.WriteAllText(Settings.FilePath, """
        {
          "ConvertKey": "Middle",
          "ToolsKey": "Right"
        }
        """);
        var s = Settings.Load();
        Assert.Equal("Shift", s.ConvertKey);
        Assert.Equal("Ctrl", s.ToolsKey);
    }

    [Fact]
    public void A_deliberately_unbound_key_stays_unbound()
    {
        Directory.CreateDirectory(Settings.Folder);
        File.WriteAllText(Settings.FilePath, """
        {
          "ConvertKey": ""
        }
        """);
        var s = Settings.Load();
        Assert.Equal(string.Empty, s.ConvertKey);
    }

    [Fact]
    public void The_bindable_keys_are_the_four_that_work()
    {
        // The settings dropdown offers exactly Keys.All, so this pins what a user can pick.
        Assert.Equal(new[] { "Shift", "Ctrl", "Alt", "Win" }, Keys.All);
        Assert.Empty(Keys.Unusable.Intersect(Keys.All));
    }

    [Fact]
    public void Every_bindable_key_has_a_label_and_none_is_offered_too()
    {
        // "None" is offered first so a tab can be switched off and a broken binding replaced.
        foreach (var lang in new[] { "ar", "en" })
        {
            Loc.Set(lang);
            Assert.False(string.IsNullOrWhiteSpace(Keys.Label(Keys.None)), lang);
            foreach (var key in Keys.All) Assert.False(string.IsNullOrWhiteSpace(Keys.Label(key)), $"{lang}/{key}");
        }
        Loc.Set("ar");
    }

    [Fact]
    public void Rebinding_moves_the_summon_to_the_new_key()
    {
        var s = new Settings { ConvertKey = "Win" };
        var m = Dragging(s);
        Assert.Null(m.Poll(KeyState.Shift, DateTime.UtcNow));   // Shift no longer means convert
        Assert.Equal(MenuTab.Convert, m.Poll(KeyState.Win, DateTime.UtcNow));
    }

    [Fact]
    public void An_unbound_key_summons_nothing()
    {
        var s = new Settings { ActionsKey = "" };
        var m = Dragging(s);
        Assert.Null(m.Poll(KeyState.Alt, DateTime.UtcNow));
        Assert.Equal(MenuTab.Tools, m.Poll(KeyState.Ctrl, DateTime.UtcNow)); // the others still work
    }

    // ------------------------------------------------------------------ palette

    [Fact]
    public void Light_and_dark_are_genuinely_different()
    {
        var s = new Settings { ThemePreset = "fluent" };
        var light = Theme.PaletteFor(s, dark: false);
        var dark = Theme.PaletteFor(s, dark: true);
        Assert.False(light.IsDark);
        Assert.True(dark.IsDark);
        Assert.NotEqual(light.WheelFill, dark.WheelFill);
        Assert.NotEqual(light.Text, dark.Text);
    }

    [Fact]
    public void The_same_settings_always_produce_the_same_palette()
    {
        var s = new Settings { ThemePreset = "vivid", Accent = "rose", AccentStrength = 1.2, Contrast = 1.3 };
        Assert.Equal(Theme.PaletteFor(s, true), Theme.PaletteFor(s, true));
    }

    [Fact]
    public void Accent_changes_reach_the_ring_fill()
    {
        var a = Theme.PaletteFor(new Settings { ThemePreset = "vivid", Accent = "teal" }, true);
        var b = Theme.PaletteFor(new Settings { ThemePreset = "vivid", Accent = "rose" }, true);
        Assert.NotEqual(a.Accent, b.Accent);
        Assert.NotEqual(a.WheelFill, b.WheelFill);
    }

    [Fact]
    public void A_custom_hex_wins_over_the_named_accent()
    {
        var p = Theme.PaletteFor(new Settings { Accent = "teal", AccentHex = "#FF8800", AccentStrength = 1.0 }, true);
        Assert.True(p.Accent.R > p.Accent.B, "the custom orange has to survive the palette");
    }

    [Fact]
    public void Every_preset_is_reachable_by_id_and_has_copy()
    {
        Assert.NotEmpty(Theme.PresetIds);
        foreach (var id in Theme.PresetIds)
        {
            Assert.NotEqual("theme." + id, Loc.T("theme." + id));
            Assert.NotEqual("theme." + id + ".desc", Loc.T("theme." + id + ".desc"));
        }
    }

    [Fact]
    public void Every_named_accent_resolves_to_a_colour()
    {
        foreach (var (id, _) in Theme.Accents)
            Assert.NotEqual(default, Theme.AccentFor(id));
    }

    // ------------------------------------------------------------------ geometry

    [Fact]
    public void Auto_picks_a_layout_from_the_number_of_sectors()
    {
        var look = new Settings { Layout = MenuLayout.Auto };
        Assert.Equal(MenuLayout.Orbit, RadialMenu.Measure(look, 3).Layout);
        Assert.Equal(MenuLayout.Donut, RadialMenu.Measure(look, 6).Layout);
        Assert.Equal(MenuLayout.Pie, RadialMenu.Measure(look, 8).Layout);
    }

    [Fact]
    public void An_explicit_layout_is_never_overridden()
    {
        var look = new Settings { Layout = MenuLayout.Pie };
        Assert.Equal(MenuLayout.Pie, RadialMenu.Measure(look, 2).Layout);
        look.Layout = MenuLayout.Orbit;
        Assert.Equal(MenuLayout.Orbit, RadialMenu.Measure(look, 8).Layout);
    }

    [Fact]
    public void A_bigger_diameter_makes_a_bigger_ring()
    {
        var small = RadialMenu.Measure(new Settings { MenuDiameter = 260 }, 6);
        var large = RadialMenu.Measure(new Settings { MenuDiameter = 500 }, 6);
        Assert.True(large.Size > small.Size);
        Assert.True(large.OuterRadius > small.OuterRadius);
    }

    [Fact]
    public void Density_scales_the_roomy_ring_without_changing_its_diameter()
    {
        var compact = RadialMenu.Measure(new Settings { Density = Density.Compact }, 6);
        var large = RadialMenu.Measure(new Settings { Density = Density.Large }, 6);
        Assert.True(large.RingWidth > compact.RingWidth);
        Assert.Equal(compact.OuterRadius, large.OuterRadius, 3);
    }

    [Fact]
    public void Only_the_open_layouts_put_labels_outside()
    {
        Assert.Equal(MenuLayout.Donut, RadialMenu.Measure(new Settings { Layout = MenuLayout.Donut }, 6).Layout);
        var donut = RadialMenu.Measure(new Settings { Layout = MenuLayout.Donut }, 6);
        var pie = RadialMenu.Measure(new Settings { Layout = MenuLayout.Pie }, 6);
        var orbit = RadialMenu.Measure(new Settings { Layout = MenuLayout.Orbit }, 6);
        // Pie draws its labels inside the wedge; the other two need room around the rim.
        Assert.True(pie.Size < donut.Size);
        Assert.True(orbit.Size >= donut.Size);
        Assert.True(donut.HubRadius > 0 && pie.HubRadius > 0 && orbit.HubRadius > 0);
    }

    // ------------------------------------------------------------------ files

    [Fact]
    public void Saving_writes_settings_and_a_separate_theme_file()
    {
        var s = new Settings { ThemePreset = "vivid", Layout = MenuLayout.Orbit, Accent = "rose" };
        s.Save();
        Assert.True(File.Exists(Settings.FilePath));
        Assert.True(File.Exists(Settings.ThemePath));

        var loaded = Settings.Load();
        Assert.Equal("vivid", loaded.ThemePreset);
        Assert.Equal(MenuLayout.Orbit, loaded.Layout);
        Assert.Equal("rose", loaded.Accent);
    }

    [Fact]
    public void The_theme_file_wins_over_the_look_inside_settings()
    {
        var s = new Settings { ThemePreset = "paper", Layout = MenuLayout.Pie };
        s.Save();

        // A theme dragged in by hand replaces the stored look, which is the point of one file.
        var replacement = Look.From(new Settings { ThemePreset = "terminal", Layout = MenuLayout.Orbit, Accent = "lime" });
        File.WriteAllText(Settings.ThemePath, JsonSerializer.Serialize(replacement));

        var loaded = Settings.Load();
        Assert.Equal("terminal", loaded.ThemePreset);
        Assert.Equal(MenuLayout.Orbit, loaded.Layout);
        Assert.Equal("lime", loaded.Accent);
    }

    [Fact]
    public void An_exported_theme_imports_back_identically()
    {
        var source = new Settings
        {
            ThemePreset = "fluent",
            Theme = ColorMode.Dark,
            Layout = MenuLayout.Donut,
            Density = Density.Large,
            Accent = "violet",
            Gap = 7,
            FrameThickness = 2,
            RotatedLabels = true,
            ShowTabBar = false,
        };
        var json = source.ExportThemeJson();

        var target = new Settings();
        Assert.True(target.ImportThemeJson(json));

        Assert.Equal(source.ThemePreset, target.ThemePreset);
        Assert.Equal(source.Theme, target.Theme);
        Assert.Equal(source.Layout, target.Layout);
        Assert.Equal(source.Density, target.Density);
        Assert.Equal(source.Accent, target.Accent);
        Assert.Equal(source.Gap, target.Gap);
        Assert.Equal(source.FrameThickness, target.FrameThickness);
        Assert.Equal(source.RotatedLabels, target.RotatedLabels);
        Assert.Equal(source.ShowTabBar, target.ShowTabBar);
    }

    [Fact]
    public void Importing_something_that_is_not_a_theme_fails_quietly()
    {
        var s = new Settings();
        Assert.False(s.ImportThemeJson("{ not json"));
        Assert.False(s.ImportThemeJson("null"));
        Assert.Equal("windows", s.ThemePreset); // untouched
    }

    [Fact]
    public void Hand_edited_files_may_carry_comments_and_trailing_commas()
    {
        Directory.CreateDirectory(Settings.Folder);
        File.WriteAllText(Settings.FilePath, """
        {
          // the ring I like
          "Language": "en",
          "Layout": "Orbit",
          "MenuDiameter": 400,
        }
        """);
        var s = Settings.Load();
        Assert.Equal("en", s.Language);
        Assert.Equal(MenuLayout.Orbit, s.Layout);
        Assert.Equal(400, s.MenuDiameter);
    }

    [Fact]
    public void Out_of_range_values_are_clamped_rather_than_trusted()
    {
        Directory.CreateDirectory(Settings.Folder);
        File.WriteAllText(Settings.FilePath, """
        {
          "MenuDiameter": 5000,
          "Gap": -12,
          "FrameThickness": 44,
          "Contrast": 9,
          "AccentHex": "red!",
          "ConvertKey": "Spacebar"
        }
        """);
        var s = Settings.Load();
        Assert.Equal(520, s.MenuDiameter);
        Assert.Equal(0, s.Gap);
        Assert.Equal(4, s.FrameThickness);
        Assert.Equal(1.6, s.Contrast);
        Assert.Null(s.AccentHex);
        // A key that cannot open a menu falls back to the default, so the tab stays reachable.
        Assert.Equal("Shift", s.ConvertKey);
    }

    // ------------------------------------------------------------------ language

    [Fact]
    public void The_interface_follows_the_operating_system_by_default()
    {
        var s = new Settings();
        Assert.Equal(LanguageMode.System, s.LanguageMode);
        // The system decides, so the resolved language has to be one we actually ship.
        Assert.Contains(s.ResolvedLanguage(), Loc.Languages);
    }

    [Fact]
    public void A_fixed_language_ignores_the_operating_system()
    {
        Assert.Equal("ar", Loc.Resolve(LanguageMode.Arabic));
        Assert.Equal("en", Loc.Resolve(LanguageMode.English));
    }

    [Fact]
    public void The_system_language_is_resolved_from_the_live_windows_setting()
    {
        var lang = Loc.SystemLanguage();
        // Null means Windows is in a language we do not ship, which is a valid answer.
        if (lang is not null) Assert.Contains(lang, Loc.Languages);
    }

    [Fact]
    public void An_arabic_windows_makes_the_interface_arabic_and_right_to_left()
    {
        // The check runs against the real machine, so it asserts the rule, not a fixed value.
        var expected = Loc.SystemLanguage() == "ar" ? "ar" : Loc.SystemLanguage() ?? "ar";
        Assert.Equal(expected, Loc.Resolve(LanguageMode.System));
        if (expected == "ar")
        {
            Assert.True(Loc.IsRtl);
            Assert.Equal(System.Windows.FlowDirection.RightToLeft, Loc.Flow);
        }
    }

    [Fact]
    public void An_old_settings_file_that_chose_english_stays_english()
    {
        Directory.CreateDirectory(Settings.Folder);
        File.WriteAllText(Settings.FilePath, """{ "Language": "en" }""");
        // English was a deliberate choice in the old file, so it is honoured.
        Assert.Equal(LanguageMode.English, Settings.Load().LanguageMode);
    }

    [Fact]
    public void An_old_settings_file_with_the_old_arabic_default_follows_windows()
    {
        Directory.CreateDirectory(Settings.Folder);
        File.WriteAllText(Settings.FilePath, """{ "Language": "ar" }""");
        // Arabic was only the old default, so it must not pin the language on a machine
        // that Windows is showing in another language.
        Assert.Equal(LanguageMode.System, Settings.Load().LanguageMode);
    }

    [Fact]
    public void A_new_settings_file_is_not_migrated()
    {
        Directory.CreateDirectory(Settings.Folder);
        File.WriteAllText(Settings.FilePath, """{ "LanguageMode": "System", "Language": "ar" }""");
        Assert.Equal(LanguageMode.System, Settings.Load().LanguageMode);
    }

    [Fact]
    public void The_language_mode_survives_a_save_and_load()
    {
        var s = new Settings { LanguageMode = LanguageMode.English };
        s.Save();
        Assert.Equal(LanguageMode.English, Settings.Load().LanguageMode);
    }

    [Fact]
    public void An_unknown_language_mode_falls_back_to_following_windows()
    {
        Directory.CreateDirectory(Settings.Folder);
        File.WriteAllText(Settings.FilePath, """{ "LanguageMode": "Klingon" }""");
        Assert.Equal(LanguageMode.System, Settings.Load().LanguageMode);
    }

    [Fact]
    public void Every_language_has_a_system_option_label()
    {
        Assert.NotEqual("lang.system", Loc.T("en", "lang.system"));
        Assert.NotEqual("lang.system", Loc.T("ar", "lang.system"));
    }

    [Fact]
    public void The_language_follows_the_windows_preference_list()
    {
        // The preference list is what the user edits in Windows' own language settings.
        // It is the source that changes the moment they change it, so the resolution
        // has to start there rather than with a value Windows only fixes at sign-in.
        var preferred = ReadPreferredUiLanguages();
        if (preferred.Count == 0) return; // Not every machine publishes one.
        foreach (var tag in preferred)
        {
            var expected = tag.Replace('_', '-');
            var matches = expected.Equals("ar", StringComparison.OrdinalIgnoreCase)
                          || expected.StartsWith("ar-", StringComparison.OrdinalIgnoreCase)
                          || expected.Equals("en", StringComparison.OrdinalIgnoreCase)
                          || expected.StartsWith("en-", StringComparison.OrdinalIgnoreCase);
            if (matches)
            {
                // The first language we actually ship in the list decides.
                Assert.Equal(expected[..2].ToLowerInvariant(), Loc.SystemLanguage());
                return;
            }
        }
    }

    [Fact]
    public void A_pinned_language_is_not_overridden_by_the_system()
    {
        // Even on a machine whose Windows is in English, a pinned Arabic stays Arabic.
        Assert.Equal("ar", Loc.Resolve(LanguageMode.Arabic));
        Assert.Equal("en", Loc.Resolve(LanguageMode.English));
    }

    /// <summary>Reads the same registry list the app follows.</summary>
    static List<string> ReadPreferredUiLanguages()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var list = key?.GetValue("PreferredUILanguages") as string[]
                       ?? key?.GetValue("PreferredUILanguagesPending") as string[];
            return list is null ? new List<string>() : list.ToList();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    // ------------------------------------------------------------------ helpers

    static DragStateMachine Dragging(Settings settings)
    {
        var m = new DragStateMachine
        {
            Settings = settings,
            ThresholdX = 4,
            ThresholdY = 4,
            PauseDelay = TimeSpan.FromSeconds(5),
        };
        var t0 = DateTime.UtcNow;
        m.OnDown(new DragDone.Native.Win32.POINT { X = 100, Y = 100 }, eligible: true);
        m.OnMove(new DragDone.Native.Win32.POINT { X = 140, Y = 140 }, KeyState.None, t0);
        return m;
    }
}
