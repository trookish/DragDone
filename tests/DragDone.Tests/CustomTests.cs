using System.IO;
using System.Text.Json;
using DragDone.Core;
using DragDone.Processors;
using Xunit;

namespace DragDone.Tests;

/// <summary>Office format targets, user-defined actions and user-defined file types.</summary>
[Collection("FileKinds")]
public sealed class CustomTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "dd-custom-" + Guid.NewGuid().ToString("N")[..8]);

    public CustomTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        FileKinds.SetExtra(null);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string Touch(string name, string text = "x")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    static readonly Capabilities NoOffice = new(true, false, Array.Empty<string>());

    // ------------------------------------------------------------------ office

    [Theory]
    [InlineData("report.docx", new[] { "pdf", "docx", "doc", "odt", "rtf", "txt", "html" })]
    [InlineData("sheet.xlsx", new[] { "pdf", "xlsx", "xls", "ods", "csv" })]
    [InlineData("deck.pptx", new[] { "pdf", "pptx", "ppt", "odp" })]
    public void Office_documents_offer_their_applications_formats(string name, string[] tokens)
    {
        var ring = ActionCatalog.Build(new DragPayload(new[] { Touch(name) }), MenuTab.Convert, Capabilities.All);
        Assert.Equal(tokens.Select(t => "convert:" + t), ring.Select(a => a.Id));
        // The file's own format is dimmed, everything else is live.
        var own = Path.GetExtension(name).TrimStart('.');
        Assert.All(ring, a => Assert.Equal(a.Id == "convert:" + own, !a.Enabled));
    }

    [Fact]
    public void Office_targets_need_office_but_csv_to_pdf_does_not()
    {
        var docx = ActionCatalog.Build(new DragPayload(new[] { Touch("a.docx") }), MenuTab.Convert, NoOffice);
        Assert.All(docx, a => Assert.False(a.Enabled));

        var csv = ActionCatalog.Build(new DragPayload(new[] { Touch("a.csv") }), MenuTab.Convert, NoOffice);
        Assert.True(csv.Single(a => a.Id == "convert:pdf").Enabled);
        Assert.False(csv.Single(a => a.Id == "convert:xlsx").Enabled);
    }

    [Fact]
    public void Mixed_office_files_fall_back_to_pdf_only()
    {
        var ring = ActionCatalog.Build(new DragPayload(new[] { Touch("a.docx"), Touch("b.xlsx") }), MenuTab.Convert, Capabilities.All);
        Assert.Equal(new[] { "convert:pdf" }, ring.Select(a => a.Id));
    }

    [Fact]
    public void Every_office_target_has_a_save_format_code()
    {
        foreach (var t in FileKinds.OfficeTargets(OfficeApp.Word).Where(t => t != "pdf")) DocumentOps.WordFormat(t);
        foreach (var t in FileKinds.OfficeTargets(OfficeApp.Excel).Where(t => t != "pdf")) DocumentOps.ExcelFormat(t);
        foreach (var t in FileKinds.OfficeTargets(OfficeApp.PowerPoint)) DocumentOps.PowerPointFormat(t);
        Assert.Equal(16, DocumentOps.WordFormat("docx"));
        Assert.Equal(51, DocumentOps.ExcelFormat("xlsx"));
        Assert.Equal(24, DocumentOps.PowerPointFormat("pptx"));
    }

    // ------------------------------------------------------------------ custom actions

    [Theory]
    [InlineData("*", "a.png", true)]
    [InlineData("*", "folder", false)]
    [InlineData(".png, jpg", "a.jpg", true)]
    [InlineData("*.PNG", "a.png", true)]
    [InlineData("image", "a.heic", true)]
    [InlineData("video, audio", "a.png", false)]
    [InlineData("folder", "folder", true)]
    public void Types_match_extensions_and_kinds(string types, string name, bool expected)
    {
        var path = name == "folder" ? Directory.CreateDirectory(Path.Combine(_dir, "sub")).FullName : Touch(name);
        Assert.Equal(expected, CustomActions.Accepts(new CustomAction { Types = types, Program = "x" }, path));
    }

    [Fact]
    public void Placeholders_expand_and_quote_paths_with_spaces()
    {
        var file = @"C:\My Files\photo one.png";
        Assert.Equal(@"""C:\My Files\photo one.png"" ""C:\My Files\photo one.jxl""",
            CustomActions.Expand("{file} {out}", new[] { file }, @"C:\My Files\photo one.jxl"));
        // A template that already quotes is left alone.
        Assert.Equal(@"-i ""C:\My Files\photo one.png"" -n photo one -e png -d ""C:\My Files""",
            CustomActions.Expand("-i \"{file}\" -n {name} -e {ext} -d {dir}", new[] { file }, null));
        Assert.Equal(@"a.png ""b c.png""", CustomActions.Expand("{files}", new[] { "a.png", "b c.png" }, null));
        // Unknown braces pass through untouched.
        Assert.Equal("{width} a.png", CustomActions.Expand("{width} {file}", new[] { "a.png" }, null));
    }

    [Fact]
    public void Problems_are_reported_before_running()
    {
        Assert.Equal("custom.problem.program", CustomActions.Problem(new CustomAction()));
        Assert.Equal("custom.problem.types", CustomActions.Problem(new CustomAction { Program = "x", Types = " , " }));
        Assert.Equal("custom.problem.output", CustomActions.Problem(new CustomAction { Program = "x", Arguments = "{file} {out}" }));
        Assert.Equal("custom.problem.format", CustomActions.Problem(new CustomAction { Program = "x", Output = "t/x" }));
        Assert.Null(CustomActions.Problem(new CustomAction { Program = "x", Arguments = "{file} {out}", Output = ".JXL" }));
    }

    [Fact]
    public void Custom_actions_join_their_tab_for_matching_files_only()
    {
        var mine = new CustomAction { Name = "Optimise", Types = ".png", Program = "oxipng", Tab = MenuTab.Tools };
        var off = new CustomAction { Name = "Off", Types = "*", Program = "x", Tab = MenuTab.Tools, Enabled = false };
        var caps = Capabilities.All with { Custom = new[] { mine, off } };

        var tools = ActionCatalog.Build(new DragPayload(new[] { Touch("a.png") }), MenuTab.Tools, caps);
        var all = ActionCatalog.Flatten(tools).ToList();
        Assert.Contains(all, a => a.Id == "custom:" + mine.Id && a.Label == "Optimise");
        Assert.DoesNotContain(all, a => a.Id == "custom:" + off.Id);
        Assert.True(tools.Count <= ActionCatalog.MaxSectors);

        var jpg = ActionCatalog.Flatten(ActionCatalog.Build(new DragPayload(new[] { Touch("a.jpg") }), MenuTab.Tools, caps));
        Assert.DoesNotContain(jpg, a => a.Id.StartsWith("custom:"));
    }

    [Fact]
    public void Many_custom_actions_never_overflow_the_ring()
    {
        var many = Enumerable.Range(0, 12).Select(i => new CustomAction { Name = "A" + i, Types = "*", Program = "x", Tab = MenuTab.Tools }).ToList();
        var caps = Capabilities.All with { Custom = many };
        foreach (var file in new[] { "a.png", "a.mp4", "a.pdf", "a.txt", "a.zip" })
        {
            var ring = ActionCatalog.Build(new DragPayload(new[] { Touch(file) }), MenuTab.Tools, caps);
            Assert.True(ring.Count <= ActionCatalog.MaxSectors, file);
            Assert.All(ring, a => Assert.True(a.Children.Count <= ActionCatalog.MaxSectors));
            Assert.Contains(ActionCatalog.Flatten(ring), a => a.Id == "custom:" + many[0].Id);
        }
    }

    [Fact]
    public void A_custom_conversion_shows_its_format_and_dims_the_same_format()
    {
        var jxl = new CustomAction { Types = "image", Program = "magick", Arguments = "{file} {out}", Output = "jxl", Tab = MenuTab.Convert };
        var caps = Capabilities.All with { Custom = new[] { jxl } };
        var ring = ActionCatalog.Build(new DragPayload(new[] { Touch("a.png") }), MenuTab.Convert, caps);
        var sector = ActionCatalog.Flatten(ring).Single(a => a.Id == "custom:" + jxl.Id);
        Assert.Equal("JXL", sector.Label);
        Assert.Equal(ActionStyle.Format, sector.Style);
        Assert.True(sector.Enabled);
    }

    [Fact]
    public void Running_a_custom_conversion_creates_a_new_file_beside_the_original()
    {
        var source = Touch("note one.txt", "hello");
        var copy = new CustomAction
        {
            Types = ".txt", Program = "cmd.exe", Arguments = "/c copy /y {file} {out}", Output = "bak", Tab = MenuTab.Convert,
        };
        var result = CustomActions.Run(copy, new DragPayload(new[] { source }), new JobContext(new Settings()));
        Assert.Equal(JobOutcome.Created, result.Outcome);
        var output = Assert.Single(result.Outputs);
        Assert.Equal(Path.Combine(_dir, "note one.bak"), output);
        Assert.Equal("hello", File.ReadAllText(output));
        Assert.Equal("hello", File.ReadAllText(source));

        // A second run never overwrites the first output.
        var again = CustomActions.Run(copy, new DragPayload(new[] { source }), new JobContext(new Settings()));
        Assert.Equal(Path.Combine(_dir, "note one (2).bak"), Assert.Single(again.Outputs));
    }

    [Fact]
    public void A_failing_program_reports_its_exit_code_and_leaves_no_output()
    {
        var source = Touch("a.txt");
        var fail = new CustomAction { Types = "*", Program = "cmd.exe", Arguments = "/c exit 3", Output = "out" };
        var result = CustomActions.Run(fail, new DragPayload(new[] { source }), new JobContext(new Settings()));
        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.True(result.Detail!.Contains('3') || result.Detail.Contains('٣'), result.Detail);
        Assert.False(File.Exists(Path.Combine(_dir, "a.out")));

        var missing = new CustomAction { Types = "*", Program = "definitely-not-a-program-dd.exe" };
        Assert.Equal(JobOutcome.Failed, CustomActions.Run(missing, new DragPayload(new[] { source }), new JobContext(new Settings())).Outcome);
    }

    [Fact]
    public void The_executor_finds_custom_actions_by_id()
    {
        var source = Touch("b.txt", "hi");
        var s = new Settings();
        var copy = new CustomAction { Types = "*", Program = "cmd.exe", Arguments = "/c copy /y {file} {out}", Output = "copy" };
        s.CustomActions.Add(copy);
        var ok = ActionExecutor.Execute("custom:" + copy.Id, new DragPayload(new[] { source }), new JobContext(s));
        Assert.Equal(JobOutcome.Created, ok.Outcome);
        var gone = ActionExecutor.Execute("custom:nope", new DragPayload(new[] { source }), new JobContext(s));
        Assert.Equal(JobOutcome.Failed, gone.Outcome);
    }

    [Fact]
    public void Custom_actions_survive_a_save_round_trip()
    {
        var s = new Settings();
        s.CustomActions.Add(new CustomAction { Name = "X", Types = "image", Program = "magick", Output = "jxl", Tab = MenuTab.Convert });
        s.ExtraTypes[".jxl"] = FileKind.Image;
        var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        Assert.Contains("\"Convert\"", json);
        var back = JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        Assert.Equal("magick", Assert.Single(back.CustomActions).Program);
        Assert.Equal(FileKind.Image, back.ExtraTypes[".jxl"]);
    }

    // ------------------------------------------------------------------ file types

    [Fact]
    public void Extra_extensions_give_unknown_files_a_kind()
    {
        var raw = Touch("shot.cr3");
        Assert.Equal(FileKind.Other, FileKinds.Of(raw));
        FileKinds.SetExtra(new Dictionary<string, FileKind> { ["CR3"] = FileKind.Image, ["*.bad/x"] = FileKind.Image, [".zz"] = FileKind.Folder });
        Assert.Equal(FileKind.Image, FileKinds.Of(raw));
        Assert.Equal(FileKind.Other, FileKinds.Of(Touch("a.zz")));
        FileKinds.SetExtra(null);
        Assert.Equal(FileKind.Other, FileKinds.Of(raw));
    }

    [Theory]
    [InlineData(".JXL", ".jxl")]
    [InlineData("*.raw", ".raw")]
    [InlineData("cr3", ".cr3")]
    [InlineData("", null)]
    [InlineData(".a b", null)]
    [InlineData("../x", null)]
    public void Extensions_are_normalised(string input, string? expected) =>
        Assert.Equal(expected, FileKinds.NormalizeExtension(input));
}

[CollectionDefinition("FileKinds", DisableParallelization = true)]
public sealed class FileKindsCollection;
