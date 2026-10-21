using DragDone.Core;
using DragDone.Localization;
using DragDone.Native;
using DragDone.Shell;
using DragDone.UI;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Mods = DragDone.Shell.KeyState;

// Loc and OutputPaths are process-wide; run tests one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DragDone.Tests;

public class DragStateMachineTests
{
    static readonly DateTime T0 = new(2026, 10, 18, 12, 0, 0);
    static Win32.POINT P(int x, int y) => new() { X = x, Y = y };

    static DragStateMachine Dragging(Mods atStart = Mods.None, TriggerMode trigger = TriggerMode.Modifier)
    {
        var m = new DragStateMachine { Trigger = trigger };
        m.OnDown(P(100, 100), eligible: true);
        m.OnMove(P(120, 100), atStart, T0);
        Assert.Equal(DragPhase.Dragging, m.Phase);
        return m;
    }

    [Fact]
    public void Ineligible_press_never_summons()
    {
        var m = new DragStateMachine();
        m.OnDown(P(0, 0), eligible: false);
        m.OnMove(P(50, 50), Mods.None, T0);
        Assert.Null(m.Poll(Mods.Shift, T0));
        Assert.Equal(DragPhase.Idle, m.Phase);
    }

    [Fact]
    public void Movement_inside_threshold_is_a_click_not_a_drag()
    {
        var m = new DragStateMachine();
        m.OnDown(P(100, 100), eligible: true);
        m.OnMove(P(103, 102), Mods.None, T0);
        Assert.Equal(DragPhase.Pressed, m.Phase);
        Assert.Null(m.Poll(Mods.Shift, T0));
    }

    [Theory]
    [InlineData(Mods.Shift, MenuTab.Convert)]
    [InlineData(Mods.Ctrl, MenuTab.Tools)]
    [InlineData(Mods.Alt, MenuTab.Actions)]
    public void Each_modifier_opens_its_tab(Mods key, MenuTab expected)
    {
        var m = Dragging();
        Assert.Null(m.Poll(Mods.None, T0));
        Assert.Equal(expected, m.Poll(key, T0));
        Assert.Equal(DragPhase.Summoned, m.Phase);
    }

    [Fact]
    public void Modifier_held_before_the_drag_is_explorer_intent_until_pressed_again()
    {
        var m = Dragging(atStart: Mods.Ctrl);
        Assert.Null(m.Poll(Mods.Ctrl, T0));            // Ctrl-drag to copy: no menu
        Assert.Null(m.Poll(Mods.None, T0));            // released
        Assert.Equal(MenuTab.Tools, m.Poll(Mods.Ctrl, T0)); // pressed again: summon
    }

    [Fact]
    public void Pause_trigger_is_off_by_default()
    {
        var m = Dragging();
        Assert.Null(m.Poll(Mods.None, T0.AddSeconds(5)));
    }

    [Fact]
    public void Pause_trigger_opens_convert_after_the_delay_only_when_still()
    {
        var m = Dragging(trigger: TriggerMode.ModifierOrPause);
        Assert.Null(m.Poll(Mods.None, T0.AddMilliseconds(300)));
        m.OnMove(P(160, 130), Mods.None, T0.AddMilliseconds(400)); // moved: restart the clock
        Assert.Null(m.Poll(Mods.None, T0.AddMilliseconds(800)));
        Assert.Equal(MenuTab.Convert, m.Poll(Mods.None, T0.AddMilliseconds(1100)));
    }

    [Fact]
    public void Release_and_abandon_return_to_idle()
    {
        var m = Dragging();
        m.Poll(Mods.Shift, T0);
        m.Abandon();
        Assert.Equal(DragPhase.Idle, m.Phase);
        m = Dragging();
        m.OnUp();
        Assert.Equal(DragPhase.Idle, m.Phase);
    }
}

public class RadialGeometryTests
{
    static readonly Point C = new(150, 150);

    [Theory]
    [InlineData(0, -10, 0)]
    [InlineData(10, 0, 90)]
    [InlineData(0, 10, 180)]
    [InlineData(-10, 0, 270)]
    public void Angle_zero_is_up_and_grows_clockwise(double x, double y, double expected) =>
        Assert.Equal(expected, RadialGeometry.AngleOf(new Vector(x, y)), 6);

    [Fact]
    public void Item_zero_straddles_the_top()
    {
        Assert.Equal(0, RadialGeometry.IndexAt(350, 6));
        Assert.Equal(0, RadialGeometry.IndexAt(29.9, 6));
        Assert.Equal(1, RadialGeometry.IndexAt(30.1, 6));
        Assert.Equal(5, RadialGeometry.IndexAt(329, 6));
    }

    [Fact]
    public void Every_sector_centre_hits_itself()
    {
        for (var count = 1; count <= ActionCatalog.MaxSectors; count++)
            for (var i = 0; i < count; i++)
            {
                var p = RadialGeometry.PointAt(C, 100, RadialGeometry.CenterAngle(i, count));
                Assert.Equal((RadialZone.Ring, i), RadialGeometry.Hit(p, C, 50, 138, count));
            }
    }

    [Fact]
    public void Hub_and_outside_zones()
    {
        Assert.Equal(RadialZone.Hub, RadialGeometry.Hit(new Point(160, 150), C, 50, 138, 6).Zone);
        Assert.Equal(RadialZone.Outside, RadialGeometry.Hit(new Point(150, 0), C, 50, 138, 6).Zone);
        Assert.Equal(RadialZone.Outside, RadialGeometry.Hit(new Point(150, 60), C, 50, 138, 0).Zone);
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DragDoneTests", Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name, int bytes = 16)
    {
        var p = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllBytes(p, new byte[bytes]);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

public class PayloadAndCatalogTests
{
    static IEnumerable<string> LeafIds(DragPayload p, Capabilities caps) =>
        ActionCatalog.TabOrder.SelectMany(t => ActionCatalog.Flatten(ActionCatalog.Build(p, t, caps)))
            .Where(a => !a.HasChildren).Select(a => a.Id);

    public static TheoryData<string[]> Payloads => new()
    {
        new[] { "photo.png" }, new[] { "a.jpg", "b.heic" }, new[] { "clip.mp4" }, new[] { "voice.wav" },
        new[] { "report.pdf" }, new[] { "a.pdf", "b.pdf" }, new[] { "notes.txt" }, new[] { "deck.pptx" },
        new[] { "pack.zip" }, new[] { "photo.png", "clip.mp4", "notes.txt" }, new[] { "data.bin" },
    };

    [Fact]
    public void Payload_dedupes_and_classifies()
    {
        using var d = new TempDir();
        var a = d.File("a.png", 100);
        var b = d.File("b.PNG", 50);
        var p = new DragPayload(new[] { a, b, a.ToUpperInvariant(), " " });
        Assert.Equal(2, p.Count);
        Assert.Equal(FileKind.Image, p.Kind);
        Assert.Equal(150, p.TotalBytes);
        Assert.Equal("png", p.CommonFormat);

        var mixed = new DragPayload(new[] { a, d.File("c.mp4") });
        Assert.Equal(FileKind.Mixed, mixed.Kind);
        Assert.Null(mixed.CommonFormat);
        Assert.Equal(FileKind.Folder, new DragPayload(new[] { d.Path }).Kind);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void Rings_respect_the_sector_limit_and_have_unique_ids(string[] names)
    {
        var p = new DragPayload(names);
        foreach (var tab in ActionCatalog.TabOrder)
        {
            var ring = ActionCatalog.Build(p, tab, Capabilities.All);
            Assert.True(ring.Count <= ActionCatalog.MaxSectors, $"{tab}: {ring.Count} sectors");
            var ids = ActionCatalog.Flatten(ring).Select(a => a.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            foreach (var parent in ActionCatalog.Flatten(ring).Where(a => a.HasChildren))
            {
                Assert.True(parent.Children.Count <= ActionCatalog.MaxSectors);
                if (parent.DefaultChildId is { } def) Assert.Contains(parent.Children, c => c.Id == def);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void Every_leaf_is_interactive_or_dispatched_by_the_executor(string[] names)
    {
        var source = File.ReadAllText(Repo.PathOf("src/DragDone/Core/ActionExecutor.cs"));
        var heads = Regex.Matches(source, "case \"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        foreach (var id in LeafIds(new DragPayload(names), Capabilities.All))
        {
            var head = id.Split(':')[0];
            Assert.True(ActionExecutor.IsInteractive(id) || heads.Contains(head), $"no handler for {id}");
        }
    }

    [Fact]
    public void Current_format_is_dimmed_with_a_reason()
    {
        var ring = ActionCatalog.Build(new DragPayload(new[] { "photo.png" }), MenuTab.Convert, Capabilities.All);
        var png = Assert.Single(ring, a => a.Id == "convert:png");
        Assert.False(png.Enabled);
        Assert.Equal("reason.sameFormat", png.DisabledReasonKey);
    }

    [Fact]
    public void Media_actions_explain_a_missing_ffmpeg_instead_of_hiding()
    {
        var caps = new Capabilities(Ffmpeg: false, Office: false, Array.Empty<string>());
        var leaves = ActionCatalog.TabOrder
            .SelectMany(t => ActionCatalog.Flatten(ActionCatalog.Build(new DragPayload(new[] { "clip.mp4" }), t, caps)))
            .Where(a => a.Id.StartsWith("vid.") || a.Id is "convert:mp3" or "convert:webm")
            .ToList();
        Assert.NotEmpty(leaves);
        Assert.All(leaves, a => Assert.Equal("reason.needsFfmpeg", a.DisabledReasonKey));
    }

    [Fact]
    public void Resolve_falls_back_to_a_tab_with_actions()
    {
        var p = new DragPayload(new[] { "data.bin" });
        var tab = ActionCatalog.Resolve(p, MenuTab.Convert, Capabilities.All);
        Assert.NotEmpty(ActionCatalog.Build(p, tab, Capabilities.All));
    }
}

public class OutputPathTests
{
    [Fact]
    public void Never_overwrites_files_or_folders()
    {
        using var d = new TempDir();
        Loc.Set("en");
        var src = d.File("photo.heic");
        Assert.Equal(Path.Combine(d.Path, "photo.png"), OutputPaths.For(src, "png"));
        d.File("photo.png");
        Assert.Equal(Path.Combine(d.Path, "photo (2).png"), OutputPaths.For(src, ".png"));
        d.File("photo (2).png");
        Assert.Equal(Path.Combine(d.Path, "photo (3).png"), OutputPaths.For(src, "png"));
        Assert.Equal(Path.Combine(d.Path, "photo - compressed.png"), OutputPaths.For(src, "png", "suffix.compressed"));

        Directory.CreateDirectory(Path.Combine(d.Path, "photo - pages"));
        Assert.Equal(Path.Combine(d.Path, "photo - pages (2)"), OutputPaths.FolderNear(src, "suffix.pages"));
    }

    [Fact]
    public void Suffix_follows_the_interface_language()
    {
        using var d = new TempDir();
        Loc.Set("ar");
        try
        {
            Assert.Equal(Path.Combine(d.Path, "clip - مضغوط.mp4"), OutputPaths.For(d.File("clip.mp4"), "mp4", "suffix.compressed"));
        }
        finally { Loc.Set("en"); }
    }

    [Fact]
    public void Output_folder_override_is_used_only_when_it_exists()
    {
        using var d = new TempDir();
        using var outDir = new TempDir();
        var src = d.File("a.png");
        try
        {
            OutputPaths.OutputFolderOverride = outDir.Path;
            Assert.Equal(outDir.Path, Path.GetDirectoryName(OutputPaths.For(src, "jpg")));
            OutputPaths.OutputFolderOverride = Path.Combine(outDir.Path, "missing");
            Assert.Equal(d.Path, Path.GetDirectoryName(OutputPaths.For(src, "jpg")));
        }
        finally { OutputPaths.OutputFolderOverride = null; }
    }
}

public class LocalizationTests
{
    [Fact]
    public void Arabic_is_right_to_left()
    {
        Loc.Set("ar");
        try
        {
            Assert.True(Loc.IsRtl);
            Assert.Equal(FlowDirection.RightToLeft, Loc.Flow);
            Assert.Equal("١٢٣", Loc.Num(123));
            Assert.Equal("٢٫٣ م.ب", Loc.Size(2_400_000));
        }
        finally { Loc.Set("en"); }
        Assert.Equal("2.3 MB", Loc.Size(2_400_000));
        Assert.Equal(FlowDirection.LeftToRight, Loc.Flow);
    }

    [Fact]
    public void Catalogues_have_identical_keys_and_no_blank_values()
    {
        var ar = Loc.Catalog("ar");
        var en = Loc.Catalog("en");
        Assert.Empty(ar.Keys.Except(en.Keys));
        Assert.Empty(en.Keys.Except(ar.Keys));
        Assert.All(ar.Concat(en), kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key));
    }

    [Fact]
    public void Placeholders_match_between_languages()
    {
        var en = Loc.Catalog("en");
        foreach (var (key, arValue) in Loc.Catalog("ar"))
        {
            static string[] Holes(string s) => Regex.Matches(s, @"\{\w+\}").Select(m => m.Value).OrderBy(x => x).ToArray();
            Assert.True(Holes(arValue).SequenceEqual(Holes(en[key])), key);
        }
    }

    // Literals such as "settings.json" and "error.log" are file names, not catalogue keys.
    static readonly HashSet<string> FileNames = new() { ".json", ".log" };

    [Fact]
    public void Every_key_used_in_code_exists()
    {
        var prefixes = new[] { "action", "hint", "reason", "result", "error", "suffix", "settings", "tray", "welcome", "crop", "rename", "shelf", "chip", "caption", "tab", "kind", "unit", "folder", "accent", "common", "app", "key", "theme", "layout", "density", "labels", "mode", "trigger", "scope" };
        var pattern = new Regex("\"((?:" + string.Join("|", prefixes) + @")\.[A-Za-z0-9.]+)""");
        var catalog = Loc.Catalog("ar");
        var missing = Directory.EnumerateFiles(Repo.PathOf("src/DragDone"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => pattern.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Where(k => !k.EndsWith('.') && !FileNames.Contains(Path.GetExtension(k)) && !catalog.ContainsKey(k))
            .Distinct()
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_file_kind_has_names()
    {
        foreach (var kind in Enum.GetValues<FileKind>())
        {
            Assert.True(Loc.Catalog("ar").ContainsKey($"kind.{kind.ToString().ToLowerInvariant()}"), kind.ToString());
            Assert.True(Loc.Catalog("ar").ContainsKey($"kind.{kind.ToString().ToLowerInvariant()}.plural"), kind.ToString());
        }
    }
}

static class Repo
{
    static readonly string Root = Find();

    static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DragDone.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    public static string PathOf(string relative) => Path.Combine(Root, relative);
}
