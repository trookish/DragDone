using System.IO;
using System.Text.Json;
using DragDone.Core;
using DragDone.UI;
using Xunit;

namespace DragDone.Tests;

/// <summary>
/// Shapes, opening animations and hover effects: the geometry rules and the motion arithmetic
/// that the renderer draws from. All pure, so they run without a window.
/// </summary>
public sealed class MotionTests
{
    public static TheoryData<MenuLayout> AllLayouts()
    {
        var data = new TheoryData<MenuLayout>();
        foreach (var layout in Enum.GetValues<MenuLayout>()) data.Add(layout);
        return data;
    }

    public static TheoryData<OpenAnimation> AllAnimations()
    {
        var data = new TheoryData<OpenAnimation>();
        foreach (var a in Enum.GetValues<OpenAnimation>()) data.Add(a);
        return data;
    }

    [Fact]
    public void There_are_at_least_nine_shapes_ten_animations_and_nine_hover_effects()
    {
        Assert.True(Enum.GetValues<MenuLayout>().Length >= 9);
        Assert.Equal(10, Enum.GetValues<OpenAnimation>().Length);
        Assert.True(Enum.GetValues<HoverEffect>().Length >= 9);
    }

    [Fact]
    public void The_settings_dropdown_lists_every_shape_once()
    {
        Assert.Equal(Enum.GetValues<MenuLayout>().OrderBy(l => l), SettingsWindow.ShapeOrder.OrderBy(l => l));
        Assert.Equal(MenuLayout.Auto, SettingsWindow.ShapeOrder[0]);
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void Every_shape_maps_each_sector_centre_back_to_itself(MenuLayout layout)
    {
        foreach (var count in new[] { 2, 3, 5, 6, 8, 11 })
        {
            var geo = RadialMenu.Measure(new Settings { Layout = layout }, count);
            for (var i = 0; i < count; i++)
                Assert.Equal(i, geo.IndexAt(geo.Mid(i)));
        }
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void Every_shape_fits_inside_its_own_square(MenuLayout layout)
    {
        var geo = RadialMenu.Measure(new Settings { Layout = layout, MenuDiameter = 520, Density = Density.Large }, 6);
        Assert.True(geo.HitOuter < geo.Reach);
        Assert.True(geo.HubRadius > 0);
        Assert.Equal(geo.Reach * 2, geo.Size, 3);
    }

    [Theory]
    [InlineData(MenuLayout.Orbit, BadgeShape.Circle)]
    [InlineData(MenuLayout.Honeycomb, BadgeShape.Hexagon)]
    [InlineData(MenuLayout.Tiles, BadgeShape.Tile)]
    [InlineData(MenuLayout.Diamonds, BadgeShape.Diamond)]
    [InlineData(MenuLayout.Donut, BadgeShape.None)]
    [InlineData(MenuLayout.Pie, BadgeShape.None)]
    [InlineData(MenuLayout.Petals, BadgeShape.None)]
    [InlineData(MenuLayout.Fan, BadgeShape.None)]
    public void Badge_shapes_follow_the_layout(MenuLayout layout, BadgeShape badge)
    {
        var geo = RadialMenu.Measure(new Settings { Layout = layout }, 5);
        Assert.Equal(badge, geo.Badge);
        Assert.Equal(badge != BadgeShape.None, geo.IsBadges);
    }

    [Fact]
    public void The_fan_is_a_half_ring_above_the_cursor()
    {
        var geo = RadialMenu.Measure(new Settings { Layout = MenuLayout.Fan }, 4);
        Assert.False(geo.IsFull);
        Assert.Equal(180, geo.Sweep, 3);
        // Up is 0 degrees: every slot of the fan sits in the upper half (270..360 or 0..90).
        for (var i = 0; i < 4; i++)
        {
            var mid = geo.Mid(i);
            Assert.True(mid >= 270 || mid <= 90, $"slot {i} at {mid}");
        }
        // The lower half is not part of the ring.
        Assert.Equal(-1, geo.IndexAt(180));
    }

    [Fact]
    public void Full_rings_cover_every_angle()
    {
        var geo = RadialMenu.Measure(new Settings { Layout = MenuLayout.Donut }, 7);
        for (var a = 0.0; a < 360; a += 7.5)
            Assert.InRange(geo.IndexAt(a), 0, 6);
    }

    [Theory]
    [MemberData(nameof(AllAnimations))]
    public void Every_animation_starts_hidden_or_small_and_ends_settled(OpenAnimation anim)
    {
        for (var i = 0; i < 6; i++)
            Assert.Equal(1, Motion.SectorProgress(anim, 1, i, 6), 6);
        Assert.Equal(1, Motion.HubProgress(anim, 1), 6);
        if (anim != OpenAnimation.None)
            Assert.Equal(0, Motion.SectorProgress(anim, 0, 0, 6), 6);
    }

    [Fact]
    public void Staggered_animations_open_sectors_in_order()
    {
        foreach (var anim in new[] { OpenAnimation.Cascade, OpenAnimation.Ripple, OpenAnimation.Bloom })
        {
            Assert.True(Motion.IsPerSector(anim));
            var first = Motion.SectorProgress(anim, 0.4, 0, 6);
            var last = Motion.SectorProgress(anim, 0.4, 5, 6);
            Assert.True(first > last, $"{anim}: {first} <= {last}");
        }
        Assert.False(Motion.IsPerSector(OpenAnimation.Pop));
    }

    [Fact]
    public void Easing_curves_start_at_zero_and_end_at_one()
    {
        foreach (var curve in new Func<double, double>[] { Motion.CubicOut, Motion.QuadInOut, t => Motion.BackOut(t), Motion.ElasticOut, Motion.BounceOut })
        {
            Assert.Equal(0, curve(0), 3);
            Assert.Equal(1, curve(1), 3);
        }
        // Overshooting curves really do pass the target before settling.
        Assert.True(Motion.BackOut(0.7) > 1);
    }

    [Fact]
    public void Hover_smoothing_is_frame_rate_independent()
    {
        double Run(double frameMs)
        {
            var v = 0.0;
            for (var elapsed = 0.0; elapsed < 70; elapsed += frameMs) v = Motion.Approach(v, 1, frameMs, 140);
            return v;
        }
        Assert.Equal(Run(16.7), Run(6.9), 1);
        Assert.Equal(1, Motion.Approach(0.3, 1, 16, 0));
        // It always arrives exactly, so the frame loop can stop.
        var x = 0.0;
        for (var k = 0; k < 200; k++) x = Motion.Approach(x, 1, 16, 140);
        Assert.Equal(1, x);
    }

    [Fact]
    public void Motion_settings_round_trip_and_are_clamped()
    {
        var s = new Settings { Animation = OpenAnimation.Ripple, Hover = HoverEffect.Spotlight, AnimationMs = 5000, HoverMs = -3, Layout = MenuLayout.Honeycomb };
        var copy = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s))!;
        Assert.Equal(OpenAnimation.Ripple, copy.Animation);
        Assert.Equal(HoverEffect.Spotlight, copy.Hover);
        Assert.Equal(MenuLayout.Honeycomb, copy.Layout);

        // A hand-edited or corrupted settings file still loads into valid values.
        var bad = JsonSerializer.Deserialize<Settings>("{\"Animation\":99,\"Hover\":99,\"AnimationMs\":5000,\"HoverMs\":-3}")!;
        var loaded = new Settings();
        Assert.True(loaded.ImportThemeJson(JsonSerializer.Serialize(Look.From(bad))));
        bad = loaded;
        Assert.Equal(OpenAnimation.Pop, bad.Animation);
        Assert.Equal(HoverEffect.Grow, bad.Hover);
        Assert.Equal(800, bad.AnimationMs);
        Assert.Equal(0, bad.HoverMs);
    }

    [Fact]
    public void Appearance_files_carry_the_motion_settings()
    {
        var s = new Settings { Animation = OpenAnimation.Bloom, Hover = HoverEffect.Glow, AnimationMs = 420, Layout = MenuLayout.Petals };
        var target = new Settings();
        Look.From(s).CopyTo(target);
        Assert.Equal(OpenAnimation.Bloom, target.Animation);
        Assert.Equal(HoverEffect.Glow, target.Hover);
        Assert.Equal(420, target.AnimationMs);
        Assert.Equal(MenuLayout.Petals, target.Layout);
    }

    [Fact]
    public void Every_shape_animation_and_effect_has_a_label_in_both_languages()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Localization");
        if (!Directory.Exists(folder))
            folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "DragDone", "Localization"));
        foreach (var file in new[] { "en.json", "ar.json" })
        {
            var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(folder, file)))!;
            foreach (var l in Enum.GetValues<MenuLayout>()) Assert.True(keys.ContainsKey("layout." + l.ToString().ToLowerInvariant()), $"{file}: layout {l}");
            foreach (var a in Enum.GetValues<OpenAnimation>()) Assert.True(keys.ContainsKey("animation." + a.ToString().ToLowerInvariant()), $"{file}: animation {a}");
            foreach (var h in Enum.GetValues<HoverEffect>()) Assert.True(keys.ContainsKey("hover." + h.ToString().ToLowerInvariant()), $"{file}: hover {h}");
        }
    }
}
