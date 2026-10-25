using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DragDone.Core;
using DragDone.Localization;
using DragDone.Processors;

namespace DragDone.Tests;

/// <summary>
/// Which application owns a document, and what the drag therefore says and offers. Detection is by
/// extension, so none of this needs Office installed.
/// </summary>
public class OfficeFamilyTests
{
    [Theory]
    [InlineData("a.doc", OfficeFamily.Word)]
    [InlineData("a.DOCX", OfficeFamily.Word)]
    [InlineData("a.docm", OfficeFamily.Word)]
    [InlineData("a.dot", OfficeFamily.Word)]
    [InlineData("a.dotx", OfficeFamily.Word)]
    [InlineData("a.rtf", OfficeFamily.Word)]
    [InlineData("a.odt", OfficeFamily.Word)]
    [InlineData("a.xls", OfficeFamily.Excel)]
    [InlineData("a.xlsx", OfficeFamily.Excel)]
    [InlineData("a.xlsm", OfficeFamily.Excel)]
    [InlineData("a.xlsb", OfficeFamily.Excel)]
    [InlineData("a.ods", OfficeFamily.Excel)]
    [InlineData("a.ppt", OfficeFamily.PowerPoint)]
    [InlineData("a.pptx", OfficeFamily.PowerPoint)]
    [InlineData("a.pptm", OfficeFamily.PowerPoint)]
    [InlineData("a.pps", OfficeFamily.PowerPoint)]
    [InlineData("a.ppsx", OfficeFamily.PowerPoint)]
    [InlineData("a.odp", OfficeFamily.PowerPoint)]
    [InlineData("a.vsd", OfficeFamily.Visio)]
    [InlineData("a.vsdx", OfficeFamily.Visio)]
    [InlineData("a.pub", OfficeFamily.Publisher)]
    [InlineData("a.txt", OfficeFamily.None)]
    [InlineData("a.pdf", OfficeFamily.None)]
    [InlineData("noext", OfficeFamily.None)]
    public void Owner_is_taken_from_the_extension(string name, OfficeFamily expected) =>
        Assert.Equal(expected, FileKinds.Office(name));

    [Fact]
    public void Office_extensions_stay_documents_so_the_menu_still_appears()
    {
        foreach (var name in new[] { "a.docx", "a.xlsx", "a.pptx", "a.vsdx", "a.pub" })
        {
            Assert.Equal(FileKind.Document, FileKinds.Of(name));
            Assert.True(FileKinds.IsOfficeDocument(name));
            Assert.False(FileKinds.IsPlainText(name));
        }
    }

    [Fact]
    public void Csv_is_still_a_text_file()
    {
        Assert.Equal(FileKind.Document, FileKinds.Of("data.csv"));
        Assert.True(FileKinds.IsPlainText("data.csv"));
        Assert.False(FileKinds.IsOfficeDocument("data.csv"));
        Assert.Equal(OfficeFamily.None, FileKinds.Office("data.csv"));
        Assert.True(FileKinds.IsSpreadsheetText("data.csv"));
    }

    [Fact]
    public void Openxml_is_the_zip_based_family_only()
    {
        Assert.True(FileKinds.IsOpenXml("a.docx"));
        Assert.True(FileKinds.IsOpenXml("a.xlsx"));
        Assert.True(FileKinds.IsOpenXml("a.ppsx"));
        Assert.False(FileKinds.IsOpenXml("a.doc"));
        Assert.False(FileKinds.IsOpenXml("a.csv"));
        Assert.False(FileKinds.IsOpenXml("a.pdf"));
    }

    [Fact]
    public void A_drag_reports_the_shared_family_and_says_none_when_it_mixes()
    {
        Assert.Equal(OfficeFamily.Word, new DragPayload(new[] { "a.docx", "b.doc" }).Office);
        Assert.Equal(OfficeFamily.None, new DragPayload(new[] { "a.docx", "b.pptx" }).Office);
        Assert.Equal(OfficeFamily.None, new DragPayload(new[] { "a.docx", "b.txt" }).Office);
        Assert.Equal(OfficeFamily.None, new DragPayload(new[] { "a.txt" }).Office);
    }

    [Fact]
    public void Kind_names_name_the_application()
    {
        Loc.Set("en");
        Assert.Equal("Word document", Loc.KindName(FileKind.Document, 1, OfficeFamily.Word));
        Assert.Equal("Word documents", Loc.KindName(FileKind.Document, 3, OfficeFamily.Word));
        Assert.Equal("Spreadsheet", Loc.KindName(FileKind.Document, 1, OfficeFamily.Excel));
        Assert.Equal("Presentation", Loc.KindName(FileKind.Document, 1, OfficeFamily.PowerPoint));
        Assert.Equal("Visio drawing", Loc.KindName(FileKind.Document, 1, OfficeFamily.Visio));
        Assert.Equal("Publication", Loc.KindName(FileKind.Document, 1, OfficeFamily.Publisher));
        // A plain text file keeps the old name: nothing about it changed.
        Assert.Equal("Document", Loc.KindName(FileKind.Document, 1));
    }
}

/// <summary>The rings each family gets, and what they say when their application is missing.</summary>
public class OfficeCatalogTests
{
    static IReadOnlyList<ActionDef> Ring(Capabilities caps, MenuTab tab, params string[] names) =>
        ActionCatalog.Build(new DragPayload(names), tab, caps);

    static IEnumerable<string> Ids(IEnumerable<ActionDef> ring) =>
        ActionCatalog.Flatten(ring).Select(a => a.Id);

    [Fact]
    public void Word_offers_its_targets_and_dims_the_current_format()
    {
        var ring = Ring(Capabilities.All, MenuTab.Convert, "report.docx");
        Assert.Equal(8, ring.Count);
        Assert.Equal(
            new[] { "convert:pdf", "convert:docx", "convert:doc", "convert:rtf", "convert:odt", "convert:txt", "convert:html", "convert:xps" },
            ring.Select(a => a.Id));
        Assert.Equal("reason.sameFormat", Assert.Single(ring, a => a.Id == "convert:docx").DisabledReasonKey);
        Assert.True(Assert.Single(ring, a => a.Id == "convert:pdf").Enabled);
    }

    [Fact]
    public void Excel_offers_its_targets()
    {
        var ids = Ids(Ring(Capabilities.All, MenuTab.Convert, "costs.xlsx")).ToList();
        Assert.Equal(new[] { "convert:pdf", "convert:xlsx", "convert:xls", "convert:csv", "convert:ods", "convert:html", "convert:xps" }, ids);
    }

    [Fact]
    public void PowerPoint_offers_deck_targets_and_slide_folders()
    {
        var ring = Ring(Capabilities.All, MenuTab.Convert, "deck.pptx").ToList();
        Assert.Contains("convert:pdf", ring.Select(a => a.Id));
        Assert.Contains("convert:odp", ring.Select(a => a.Id));
        Assert.Contains("convert:pngslides", ring.Select(a => a.Id));
        Assert.Contains("convert:jpgslides", ring.Select(a => a.Id));
        // No MP4: a video sector would promise something PowerPoint cannot do reliably.
        Assert.DoesNotContain("convert:mp4", ring.Select(a => a.Id));
    }

    [Fact]
    public void Visio_and_publisher_convert_to_pdf_only()
    {
        Assert.Equal(new[] { "convert:pdf" }, Ids(Ring(Capabilities.All, MenuTab.Convert, "plan.vsdx")));
        Assert.Equal(new[] { "convert:pdf" }, Ids(Ring(Capabilities.All, MenuTab.Convert, "flyer.pub")));
    }

    [Fact]
    public void A_missing_application_dimms_every_sector_and_names_it()
    {
        var caps = new Capabilities(true, true, Array.Empty<string>(), Word: false, Excel: true, PowerPoint: true, Visio: true, Publisher: true);
        var word = Ring(caps, MenuTab.Convert, "report.docx").ToList();
        Assert.All(word, a => Assert.Equal("reason.needsWord", a.DisabledReasonKey));
        // The reason text really names the application.
        Loc.Set("en");
        Assert.Contains("Word", Loc.T("reason.needsWord"));

        // Excel is installed, so an Excel document is still enabled — except the format it is in.
        Assert.All(Ring(caps, MenuTab.Convert, "costs.xlsx"),
            a => Assert.True(a.Enabled || a.DisabledReasonKey == "reason.sameFormat", a.Id));
    }

    [Fact]
    public void The_old_all_or_nothing_capability_still_disables_office()
    {
        var caps = new Capabilities(Ffmpeg: true, Office: false, Array.Empty<string>());
        var ring = Ring(caps, MenuTab.Convert, "report.docx").ToList();
        Assert.All(ring, a => Assert.False(a.Enabled));
        // Still specific: the reason names Word, not just "Office".
        Assert.Equal("reason.needsWord", ring[0].DisabledReasonKey);
    }

    [Fact]
    public void Csv_gains_excel_targets_only_when_excel_is_installed()
    {
        using var d = new TempDir();
        var csv = d.File("data.csv", 32);

        var withoutExcel = new Capabilities(true, false, Array.Empty<string>(), Excel: false);
        Assert.Equal(new[] { "convert:pdf" }, Ids(Ring(withoutExcel, MenuTab.Convert, csv)));

        var withExcel = Ring(Capabilities.All, MenuTab.Convert, csv).Select(a => a.Id).ToList();
        Assert.Equal(new[] { "convert:pdf", "convert:xlsx", "convert:xls", "convert:ods" }, withExcel);
    }

    [Fact]
    public void Word_has_more_sectors_than_a_ring_so_the_last_ones_move_into_more()
    {
        // The ring itself is capped; the overflow has to remain reachable.
        var ring = Ring(Capabilities.All, MenuTab.Convert, "report.docx");
        Assert.True(ring.Count <= ActionCatalog.MaxSectors);
        foreach (var parent in ring.Where(a => a.HasChildren))
        {
            Assert.Equal("action.more", parent.LabelKey);
            Assert.True(parent.Children.Count > 0);
            Assert.All(parent.Children, c => Assert.DoesNotContain(c.Id, ring.Select(r => r.Id)));
        }
    }

    [Fact]
    public void Tools_for_an_openxml_document_work_with_no_office_at_all()
    {
        using var d = new TempDir();
        var docx = OfficeSamples.Docx(d.Path);
        var ring = Ring(Capabilities.None, MenuTab.Tools, docx).ToList();
        var ids = ring.Select(a => a.Id).ToList();
        foreach (var id in new[] { "doc.strip", "doc.images", "doc.text", "doc.stats", "doc.compress" })
        {
            Assert.Contains(id, ids);
            Assert.True(Assert.Single(ring, a => a.Id == id).Enabled, id);
        }
        Assert.Contains("file.zip", ids);
        Assert.Contains("file.hash", ids);
        // Those two need the application, so they must not appear at all.
        Assert.DoesNotContain("doc.sheets", ids);
        Assert.DoesNotContain("doc.slides", ids);
    }

    [Fact]
    public void Excel_and_powerpoint_tools_appear_when_their_application_is_installed()
    {
        using var d = new TempDir();
        var xlsx = OfficeSamples.Xlsx(d.Path);
        var pptx = OfficeSamples.Pptx(d.Path);
        Assert.Contains("doc.sheets", Ids(Ring(Capabilities.All, MenuTab.Tools, xlsx)));
        Assert.Contains("doc.slides", Ids(Ring(Capabilities.All, MenuTab.Tools, pptx)));

        // Without the application the sector would be a dead button, so it is not drawn at all.
        Assert.DoesNotContain("doc.sheets", Ids(Ring(Capabilities.None, MenuTab.Tools, xlsx)));
        Assert.DoesNotContain("doc.slides", Ids(Ring(Capabilities.None, MenuTab.Tools, pptx)));
    }

    [Fact]
    public void A_mixed_drag_is_dimmed_by_the_application_that_is_missing()
    {
        using var d = new TempDir();
        var docx = OfficeSamples.Docx(d.Path);
        var txt = d.File("notes.txt");

        var missing = Assert.Single(Ring(Capabilities.None, MenuTab.Convert, docx, txt), a => a.Id == "convert:pdf");
        Assert.Equal("reason.needsWord", missing.DisabledReasonKey);

        // With Word installed the same mixed drag converts: text goes through the built-in writer.
        var ready = Assert.Single(Ring(Capabilities.All, MenuTab.Convert, docx, txt), a => a.Id == "convert:pdf");
        Assert.True(ready.Enabled);
    }

    [Fact]
    public void Print_is_offered_for_every_file_kind()
    {
        using var d = new TempDir();
        foreach (var name in new[] { "a.pdf", "a.docx", "photo.png", "notes.txt" })
        {
            var print = Assert.Single(Ring(Capabilities.All, MenuTab.Actions, d.File(name)), a => a.Id == "act.print");
            Assert.True(print.Enabled, name);
        }
    }

    [Fact]
    public void Print_is_disabled_for_a_folder()
    {
        using var d = new TempDir();
        var print = Assert.Single(Ring(Capabilities.All, MenuTab.Actions, d.Path), a => a.Id == "act.print");
        Assert.Equal("reason.filesOnly", print.DisabledReasonKey);
    }

    [Fact]
    public void Every_document_leaf_is_dispatched_by_the_executor()
    {
        using var d = new TempDir();
        var files = new[]
        {
            OfficeSamples.Docx(d.Path), OfficeSamples.Docx(d.Path, "second.docx"), OfficeSamples.Xlsx(d.Path),
            OfficeSamples.Pptx(d.Path), OfficeSamples.Pptm(d.Path),
        };
        var source = File.ReadAllText(RepoPath("src/DragDone/Core/ActionExecutor.cs"));
        var heads = Regex.Matches(source, "case \"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        var leaves = files
            .SelectMany(f => ActionCatalog.TabOrder
                .SelectMany(t => ActionCatalog.Flatten(ActionCatalog.Build(new DragPayload(new[] { f }), t, Capabilities.All))))
            .Where(a => !a.HasChildren)
            .Select(a => a.Id)
            .Distinct();
        foreach (var id in leaves)
        {
            Assert.True(ActionExecutor.IsInteractive(id) || heads.Contains(id.Split(':')[0]), $"no handler for {id}");
        }
    }

    static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DragDone.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("repo root not found"), relative);
    }
}

/// <summary>
/// The OOXML tools, run against packages this test writes itself, so the whole suite works on a
/// machine with no Office.
/// </summary>
public class OpenXmlToolTests
{
    static JobContext Ctx() => new(new Settings());

    [Fact]
    public void Strip_removes_the_personal_properties_and_leaves_a_valid_package()
    {
        using var d = new TempDir();
        var docx = OfficeSamples.Docx(d.Path);
        var before = File.ReadAllBytes(docx);

        var output = OpenXmlOps.StripMetadata(docx, Ctx());

        Assert.NotEqual(docx, output);
        Assert.Equal(before, File.ReadAllBytes(docx));   // the source is never touched
        using var zip = ZipFile.OpenRead(output);
        var core = Read(zip, "docProps/core.xml");
        Assert.DoesNotContain("Turki", core);
        Assert.DoesNotContain("lastModifiedBy", core);
        Assert.DoesNotContain("description", core);
        var app = Read(zip, "docProps/app.xml");
        Assert.DoesNotContain("DragDone Labs", app);
        Assert.Null(zip.GetEntry("docProps/custom.xml"));
        Assert.DoesNotContain("custom-properties", Read(zip, "_rels/.rels"));
        // The document body survives: this is a rewrite, not a new file.
        Assert.Contains("Hello world", Read(zip, "word/document.xml"));
    }

    [Fact]
    public void Strip_reports_a_document_that_had_nothing_to_remove()
    {
        using var d = new TempDir();
        var docx = OfficeSamples.Docx(d.Path);
        var clean = OpenXmlOps.StripMetadata(docx, Ctx());
        Assert.True(File.Exists(clean));

        var again = Assert.Throws<UnchangedException>(() => OpenXmlOps.StripMetadata(clean, Ctx()));
        Assert.Equal("result.alreadyClean", again.MessageKey);
    }

    [Fact]
    public void Extract_text_keeps_paragraphs_and_arabic()
    {
        using var d = new TempDir();
        var docx = OfficeSamples.Docx(d.Path);
        var output = OpenXmlOps.ExtractText(docx, Ctx());
        Assert.Equal(".txt", Path.GetExtension(output));
        var text = File.ReadAllText(output);
        Assert.Contains("Hello world from DragDone.", text);
        Assert.Contains("مرحبا بالعالم", text);
        // Paragraphs stay on their own lines.
        Assert.True(text.IndexOf("Hello", StringComparison.Ordinal) < text.IndexOf("مرحبا", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_text_walks_slides_in_order()
    {
        using var d = new TempDir();
        var pptx = OfficeSamples.Pptx(d.Path);
        var text = File.ReadAllText(OpenXmlOps.ExtractText(pptx, Ctx()));
        Assert.Contains("First slide", text);
        Assert.Contains("Second slide", text);
        Assert.True(text.IndexOf("First slide", StringComparison.Ordinal) < text.IndexOf("Second slide", StringComparison.Ordinal));
        Assert.DoesNotContain("</a:t>", text);
    }

    [Fact]
    public void Extract_text_reads_cells_and_shared_strings()
    {
        using var d = new TempDir();
        var xlsx = OfficeSamples.Xlsx(d.Path);
        var text = File.ReadAllText(OpenXmlOps.ExtractText(xlsx, Ctx()));
        Assert.Contains("Item", text);
        Assert.Contains("Quantity", text);
        Assert.Contains("Cable", text);
        Assert.Contains("42", text);
        Assert.Contains("# Data", text);
        Assert.Contains("# Summary", text);
    }

    [Fact]
    public void Extract_images_writes_every_picture_into_a_folder()
    {
        using var d = new TempDir();
        var docx = OfficeSamples.Docx(d.Path);
        var folder = OpenXmlOps.ExtractImages(docx, Ctx());
        Assert.True(Directory.Exists(folder));
        var files = Directory.GetFiles(folder);
        Assert.Single(files);
        Assert.Equal("photo.jpg", Path.GetFileName(files[0]));
        Assert.True(new FileInfo(files[0]).Length > 0);
        // Nothing was taken out of the document itself.
        using var zip = ZipFile.OpenRead(docx);
        Assert.NotNull(zip.GetEntry("word/media/photo.jpg"));
    }

    [Fact]
    public void Extract_images_says_so_when_there_are_none()
    {
        using var d = new TempDir();
        var xlsx = OfficeSamples.Xlsx(d.Path);   // no media part
        var ex = Assert.Throws<UnchangedException>(() => OpenXmlOps.ExtractImages(xlsx, Ctx()));
        Assert.Equal("result.noImages", ex.MessageKey);
    }

    [Fact]
    public void Statistics_count_words_and_length()
    {
        using var d = new TempDir();
        var docx = OfficeSamples.Docx(d.Path);
        var stats = OpenXmlOps.ReadStats(docx);
        Assert.Equal(6, stats.Words);   // four English words plus two Arabic ones
        var expected = NonSpace("Hello world from DragDone.") + NonSpace("مرحبا بالعالم");
        Assert.Equal(expected, stats.Characters);
        Assert.Equal("stats.pages", stats.SectionsKey);

        var pptx = OpenXmlOps.ReadStats(OfficeSamples.Pptx(d.Path));
        Assert.Equal(2, pptx.Sections);
        Assert.Equal("stats.slides", pptx.SectionsKey);

        var xlsx = OpenXmlOps.ReadStats(OfficeSamples.Xlsx(d.Path));
        Assert.Equal(2, xlsx.Sections);
        Assert.Equal("stats.sheets", xlsx.SectionsKey);

        static int NonSpace(string s) => s.Count(c => !char.IsWhiteSpace(c));
    }

    [Fact]
    public void Statistics_refuse_a_format_they_cannot_read()
    {
        using var d = new TempDir();
        var pdf = d.File("a.pdf", 64);
        Assert.Throws<UserFacingException>(() => { OpenXmlOps.ReadStats(pdf); });
    }

    [Fact]
    public void Compress_shrinks_the_embedded_pictures_and_keeps_their_names()
    {
        using var d = new TempDir();
        var docx = OfficeSamples.Docx(d.Path, noise: true);
        var before = new FileInfo(docx).Length;
        var mediaBefore = OfficeSamples.MediaBytes(docx, "word/media/photo.jpg");

        var output = OpenXmlOps.CompressMedia(docx, Ctx());

        Assert.True(new FileInfo(output).Length < before, $"{before} -> {new FileInfo(output).Length}");
        var mediaAfter = OfficeSamples.MediaBytes(output, "word/media/photo.jpg");
        Assert.True(mediaAfter.Length < mediaBefore.Length);
        // Same name, still a JPEG the package can point at.
        using var zip = ZipFile.OpenRead(output);
        Assert.NotNull(zip.GetEntry("word/media/photo.jpg"));
        Assert.Equal(mediaAfter, OfficeSamples.MediaBytes(output, "word/media/photo.jpg"));
        Assert.Equal(before, new FileInfo(docx).Length);   // source untouched
        using var image = new MemoryStream(mediaAfter);
        var frame = BitmapDecoder.Create(image, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.True(frame.PixelWidth > 0);
    }

    [Fact]
    public void Compress_reports_an_already_optimal_file_and_creates_nothing()
    {
        using var d = new TempDir();
        var docx = OfficeSamples.Docx(d.Path);   // tiny picture, nothing worth re-encoding
        var ex = Assert.Throws<UnchangedException>(() => OpenXmlOps.CompressMedia(docx, Ctx()));
        Assert.Equal("result.alreadyOptimal", ex.MessageKey);
        Assert.False(File.Exists(OutputPaths.For(docx, "docx", "suffix.compressed")));
    }

    [Fact]
    public void Tools_never_write_over_their_source()
    {
        using var d = new TempDir();
        var docx = OfficeSamples.Docx(d.Path, noise: true);
        var hash = Hash(docx);
        var outputs = new List<string>
        {
            OpenXmlOps.StripMetadata(docx, Ctx()),
            OpenXmlOps.ExtractText(docx, Ctx()),
            OpenXmlOps.ExtractImages(docx, Ctx()),
            OpenXmlOps.CompressMedia(docx, Ctx()),
        };
        Assert.All(outputs, o => Assert.NotEqual(Path.GetFullPath(docx), Path.GetFullPath(o)));
        Assert.Equal(hash, Hash(docx));
        // And the scratch copies are gone, so a second run writes again instead of colliding.
        Assert.All(outputs, o => Assert.True(File.Exists(o) || Directory.Exists(o), o));

        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public void A_password_protected_package_is_told_apart_from_a_plain_one()
    {
        using var d = new TempDir();
        var docx = OfficeSamples.Docx(d.Path);
        Assert.False(OfficeOps.IsEncrypted(docx));

        // Encrypted OOXML is an OLE compound file, whatever the extension says.
        var locked = Path.Combine(d.Path, "locked.docx");
        File.WriteAllBytes(locked, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 });
        Assert.True(OfficeOps.IsEncrypted(locked));
        Assert.Throws<UserFacingException>(() => { OpenXmlOps.ReadStats(locked); });
    }

    static string Read(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException("missing " + name);
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Minimal but real OOXML packages: the zip parts the tools read, plus the properties a document
/// carries about its author. No Office involved, so these tests are deterministic.
/// </summary>
public static class OfficeSamples
{
    const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    const string DrawNs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    const string SheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static string Docx(string folder, string name = "report.docx", bool noise = false)
    {
        var path = Path.Combine(folder, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Default Extension="jpg" ContentType="image/jpeg"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            </Types>
            """);
        Add(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
              <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>
              <Relationship Id="rId4" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/custom-properties" Target="docProps/custom.xml"/>
            </Relationships>
            """);
        Add(zip, "word/document.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="{WordNs}"><w:body>
              <w:p><w:r><w:t>Hello world from DragDone.</w:t></w:r></w:p>
              <w:p><w:r><w:t>مرحبا بالعالم</w:t></w:r></w:p>
            </w:body></w:document>
            """);
        Add(zip, "docProps/core.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
                               xmlns:dc="http://purl.org/dc/elements/1.1/">
              <dc:creator>Turki Alshaalan</dc:creator>
              <cp:lastModifiedBy>Turki Alshaalan</cp:lastModifiedBy>
              <dc:description>Internal draft</dc:description>
            </cp:coreProperties>
            """);
        Add(zip, "docProps/app.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties">
              <Company>DragDone Labs</Company>
              <Manager>Nobody</Manager>
              <Pages>3</Pages>
            </Properties>
            """);
        Add(zip, "docProps/custom.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/custom-properties">
              <property name="Reviewed"/>
            </Properties>
            """);
        Add(zip, "word/media/photo.jpg", noise ? NoiseJpeg(600, 600, 100) : NoiseJpeg(32, 32, 60));
        return path;
    }

    public static string Xlsx(string folder, string name = "costs.xlsx")
    {
        var path = Path.Combine(folder, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
            </Types>
            """);
        Add(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        Add(zip, "xl/workbook.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="{SheetNs}" xmlns:r="{RelNs}"><sheets>
              <sheet name="Data" sheetId="1" r:id="rId1"/>
              <sheet name="Summary" sheetId="2" r:id="rId2"/>
            </sheets></workbook>
            """);
        Add(zip, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
              <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>
            </Relationships>
            """);
        Add(zip, "xl/sharedStrings.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <sst xmlns="{SheetNs}" count="4" uniqueCount="4">
              <si><t>Item</t></si><si><t>Quantity</t></si><si><t>Cable</t></si><si><t>Paper</t></si>
            </sst>
            """);
        Add(zip, "xl/worksheets/sheet1.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="{SheetNs}"><sheetData>
              <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>
              <row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2"><v>42</v></c></row>
            </sheetData></worksheet>
            """);
        Add(zip, "xl/worksheets/sheet2.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="{SheetNs}"><sheetData>
              <row r="1"><c r="A1" t="s"><v>3</v></c></row>
            </sheetData></worksheet>
            """);
        return path;
    }

    public static string Pptx(string folder, string name = "deck.pptx") => Presentation(folder, name);

    public static string Pptm(string folder, string name = "macro.pptm") => Presentation(folder, name);

    static string Presentation(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Default Extension="jpg" ContentType="image/jpeg"/>
            </Types>
            """);
        Add(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
            </Relationships>
            """);
        Add(zip, "ppt/presentation.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                            xmlns:r="{RelNs}"><p:sldIdLst>
              <p:sldId id="256" r:id="rId1"/>
              <p:sldId id="257" r:id="rId2"/>
            </p:sldIdLst></p:presentation>
            """);
        for (var i = 1; i <= 2; i++)
        {
            var text = i == 1 ? "First slide" : "Second slide";
            Add(zip, $"ppt/slides/slide{i}.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                       xmlns:a="{DrawNs}"><p:cSld><p:spTree>
                  <p:sp><p:txBody><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp>
                </p:spTree></p:cSld></p:sld>
                """);
        }
        Add(zip, "ppt/media/photo.jpg", NoiseJpeg(24, 24, 50));
        return path;
    }

    public static byte[] MediaBytes(string package, string part)
    {
        using var zip = ZipFile.OpenRead(package);
        var entry = zip.GetEntry(part) ?? throw new InvalidOperationException("missing " + part);
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    static void Add(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    /// <summary>Random pixels at high quality: incompressible, so a re-encode is measurably smaller.</summary>
    static byte[] NoiseJpeg(int width, int height, int quality)
    {
        var stride = width * 3;
        var pixels = new byte[stride * height];
        new Random(width * 7 + height).NextBytes(pixels);
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
