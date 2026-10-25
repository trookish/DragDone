using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using DragDone.Core;
using DragDone.Localization;

namespace DragDone.Processors;

/// <summary>
/// Tools for OOXML packages (docx, xlsx, pptx) that work with no Office installed.
///
/// A docx is a zip of XML parts, so the text, the media and the document properties are all
/// readable and rewritable directly. Every tool here opens the source read-only and writes a *new*
/// file beside it — the source is never touched, which also means a failed run cannot damage the
/// only copy of someone's document.
/// </summary>
public static class OpenXmlOps
{
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace DocRels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace Cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    static readonly XNamespace Ap = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";

    const string CorePart = "docProps/core.xml";
    const string AppPart = "docProps/app.xml";

    /// <summary>Embedded pictures below this size are not worth re-encoding.</summary>
    const long WorthCompressing = 64 * 1024;

    static readonly string[] MediaRoots = { "word/media/", "ppt/media/", "xl/media/" };

    // ------------------------------------------------------------------ strip metadata

    /// <summary>
    /// Removes the document properties that travel with a file and identify its author: everything
    /// in <c>docProps/core.xml</c> (author, last modified by, revision, comments), everything in
    /// <c>app.xml</c> (company, manager, template), the custom properties part, and the preview
    /// thumbnail. The XML parts stay in place and valid — an empty properties part is normal, a
    /// missing one is not — so the document still opens everywhere.
    /// </summary>
    public static string StripMetadata(string source, JobContext ctx)
    {
        Guard(source);
        var output = OutputPaths.For(source, FileKinds.FormatToken(source), "suffix.clean");
        File.Copy(source, output, overwrite: false);
        var removed = false;
        try
        {
            using (var zip = ZipFile.Open(output, ZipArchiveMode.Update))
            {
                removed |= Clear(zip, CorePart, CleanCore);
                removed |= Clear(zip, AppPart, CleanApp);
                removed |= Drop(zip, "docProps/custom.xml");
                foreach (var thumbnail in zip.Entries
                             .Where(e => e.FullName.StartsWith("docProps/", StringComparison.OrdinalIgnoreCase)
                                         && e.Name.StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase))
                             .ToList())
                {
                    removed |= Drop(zip, thumbnail.FullName);
                }
                removed |= DropMetadataRelationships(zip);
                ctx.Token.ThrowIfCancellationRequested();
            }
        }
        catch
        {
            Ffmpeg.TryDelete(output);
            throw;
        }

        if (!removed)
        {
            Ffmpeg.TryDelete(output);
            throw new UnchangedException("result.alreadyClean");
        }
        return output;
    }

    static XDocument CleanCore() => new(
        new XElement(Cp + "coreProperties",
            new XAttribute(XNamespace.Xmlns + "cp", Cp.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dc", Dc.NamespaceName)));

    static XDocument CleanApp() => new(
        new XElement(Ap + "Properties", new XAttribute("xmlns", Ap.NamespaceName)));

    /// <summary>
    /// Drops the package relationships that pointed at the removed parts. A relationship without a
    /// part is a corrupt package, so the two have to go together.
    /// </summary>
    static bool DropMetadataRelationships(ZipArchive zip)
    {
        var entry = zip.GetEntry("_rels/.rels");
        if (entry is null) return false;
        var doc = XDocument.Parse(ReadText(entry));
        if (doc.Root is null) return false;
        var doomed = doc.Root.Elements(R + "Relationship")
            .Where(r => ((string?)r.Attribute("Type")) is { } type
                        && (type.EndsWith("/custom-properties", StringComparison.OrdinalIgnoreCase)
                            || type.EndsWith("/metadata/thumbnail", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (doomed.Count == 0) return false;
        foreach (var rel in doomed) rel.Remove();
        Write(zip, "_rels/.rels", doc);
        return true;
    }

    // ------------------------------------------------------------------ extract images

    /// <summary>
    /// Writes every picture stored inside the package to a folder beside it, keeping the original
    /// names and bytes (<c>word/media</c>, <c>ppt/media</c>, <c>xl/media</c>).
    /// </summary>
    public static string ExtractImages(string source, JobContext ctx)
    {
        using var zip = Open(source);
        var media = zip.Entries
            .Where(e => e.Length > 0 && MediaRoots.Any(root => e.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (media.Count == 0) throw new UnchangedException("result.noImages");

        var folder = OutputPaths.FolderNear(source, "suffix.media");
        Directory.CreateDirectory(folder);
        try
        {
            for (var i = 0; i < media.Count; i++)
            {
                ctx.Token.ThrowIfCancellationRequested();
                var entry = media[i];
                var target = OutputPaths.Unique(Path.Combine(folder, Path.GetFileName(entry.FullName)));
                entry.ExtractToFile(target);
                ctx.Report((double)(i + 1) / media.Count);
            }
        }
        catch
        {
            FileOps.TryDeleteFolder(folder);
            throw;
        }
        return folder;
    }

    // ------------------------------------------------------------------ extract text

    /// <summary>
    /// Writes the readable text of a document to a UTF-8 <c>.txt</c> beside it: Word paragraphs,
    /// PowerPoint slide text in slide order, or Excel cell values sheet by sheet.
    /// </summary>
    public static string ExtractText(string source, JobContext ctx)
    {
        var text = FileKinds.Office(source) switch
        {
            OfficeFamily.Word => WordText(source),
            OfficeFamily.PowerPoint => SlideText(source),
            OfficeFamily.Excel => SheetText(source),
            _ => throw new UserFacingException(Loc.T("reason.unsupported")),
        };
        if (string.IsNullOrWhiteSpace(text)) throw new UnchangedException("result.emptyDocument");

        var output = OutputPaths.For(source, "txt", "suffix.text");
        File.WriteAllText(output, text, new UTF8Encoding(true));
        return output;
    }

    static string WordText(string source)
    {
        using var zip = Open(source);
        var entry = zip.GetEntry("word/document.xml") ?? throw new UserFacingException(Loc.T("error.office"));
        var doc = XDocument.Parse(ReadText(entry));
        var sb = new StringBuilder();
        foreach (var paragraph in doc.Descendants(W + "p"))
        {
            sb.AppendLine(ParagraphText(paragraph));
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    static string ParagraphText(XElement paragraph)
    {
        var sb = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            if (node.Name == W + "t") sb.Append(node.Value);
            else if (node.Name == W + "tab") sb.Append('\t');
            else if (node.Name == W + "br" || node.Name == W + "cr") sb.Append('\n');
        }
        return sb.ToString();
    }

    static string SlideText(string source)
    {
        using var zip = Open(source);
        var slides = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => NumberIn(e.FullName))
            .ToList();
        var blocks = new List<string>();
        foreach (var slide in slides)
        {
            var xml = XDocument.Parse(ReadText(slide));
            var lines = xml.Descendants(A + "p")
                .Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value)))
                .Where(line => line.Length > 0)
                .ToList();
            if (lines.Count > 0) blocks.Add(string.Join(Environment.NewLine, lines));
        }
        return string.Join(Environment.NewLine + Environment.NewLine, blocks).TrimEnd() + Environment.NewLine;
    }

    static string SheetText(string source)
    {
        using var zip = Open(source);
        var shared = SharedStrings(zip);
        var sb = new StringBuilder();
        foreach (var (name, path) in Sheets(zip))
        {
            var entry = zip.GetEntry(path);
            if (entry is null) continue;
            sb.AppendLine("# " + name);
            var sheet = XDocument.Parse(ReadText(entry));
            foreach (var row in sheet.Descendants(S + "row"))
            {
                var cells = new List<string>();
                foreach (var cell in row.Elements(S + "c"))
                {
                    while (cells.Count < ColumnIndex((string?)cell.Attribute("r")) - 1) cells.Add("");
                    cells.Add(CellText(cell, shared));
                }
                sb.AppendLine(string.Join("\t", cells).TrimEnd('\t'));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Sheets in workbook order, with the part path that holds each one.</summary>
    static IEnumerable<(string Name, string Path)> Sheets(ZipArchive zip)
    {
        var workbook = zip.GetEntry("xl/workbook.xml");
        if (workbook is not null)
        {
            var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (zip.GetEntry("xl/_rels/workbook.xml.rels") is { } relsEntry)
            {
                foreach (var rel in XDocument.Parse(ReadText(relsEntry)).Root?.Elements(R + "Relationship") ?? Enumerable.Empty<XElement>())
                {
                    if ((string?)rel.Attribute("Id") is not { } id) continue;
                    if ((string?)rel.Attribute("Target") is not { } target) continue;
                    targets[id] = ResolveXlPath(target);
                }
            }

            var any = false;
            foreach (var sheet in XDocument.Parse(ReadText(workbook)).Descendants(S + "sheet"))
            {
                var name = (string?)sheet.Attribute("name") ?? "Sheet";
                var id = (string?)sheet.Attribute(DocRels + "id");
                if (id is null || !targets.TryGetValue(id, out var path)) continue;
                any = true;
                yield return (name, path);
            }
            if (any) yield break;
        }

        // A workbook part we could not read is still worth dumping: fall back to the raw sheets.
        foreach (var entry in zip.Entries
                     .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                                 && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => NumberIn(e.FullName)))
        {
            yield return (Path.GetFileNameWithoutExtension(entry.FullName), entry.FullName);
        }
    }

    static string ResolveXlPath(string target) => target.StartsWith('/')
        ? target[1..]
        : "xl/" + target.Replace("../", "", StringComparison.Ordinal);

    static List<string> SharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is not { } entry) return list;
        foreach (var item in XDocument.Parse(ReadText(entry)).Descendants(S + "si"))
        {
            // Rich text keeps each run in its own <t>; plain strings have exactly one.
            list.Add(string.Concat(item.Descendants(S + "t").Select(t => t.Value)));
        }
        return list;
    }

    static string CellText(XElement cell, List<string> shared)
    {
        var type = (string?)cell.Attribute("t") ?? "n";
        var value = cell.Element(S + "v")?.Value;
        switch (type)
        {
            case "s":
                return int.TryParse(value, out var index) && index >= 0 && index < shared.Count ? shared[index] : "";
            case "inlineStr":
                return string.Concat(cell.Descendants(S + "t").Select(t => t.Value));
            case "b":
                return value == "1" ? "TRUE" : "FALSE";
            default:
                return value ?? "";
        }
    }

    /// <summary>"C7" → 3, so empty cells keep their column and the text lines up.</summary>
    static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return 1;
        var column = 0;
        foreach (var c in reference)
        {
            if (!char.IsLetter(c)) break;
            column = column * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }
        return Math.Max(1, column);
    }

    static int NumberIn(string name)
    {
        var digits = new string(Path.GetFileNameWithoutExtension(name).SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    // ------------------------------------------------------------------ statistics

    /// <summary>
    /// Counts for one document. <see cref="Sections"/> is pages, slides or sheets, and carries the
    /// keys for both its labelled and its compact form, singular and plural: "1 page" is not "1 pages".
    /// </summary>
    public sealed record Stats(
        int Words,
        int Characters,
        int Sections,
        string SectionsKey,
        string SectionsShortKey,
        string SectionsOneKey);

    /// <summary>
    /// Words, characters and length of a document, read without Office. Pages come from the
    /// application properties Word writes; when a file has none (a generated docx often does not)
    /// the count is zero and the caller simply omits it rather than inventing a number.
    /// </summary>
    public static Stats ReadStats(string source)
    {
        using var zip = Open(source);
        switch (FileKinds.Office(source))
        {
            case OfficeFamily.Word:
            {
                var text = WordPlainText(zip);
                return new Stats(CountWords(text), CountCharacters(text), PageCount(zip), "stats.pages", "stats.pagesShort", "stats.pageOne");
            }
            case OfficeFamily.PowerPoint:
            {
                var text = SlidePlainText(zip);
                var slides = zip.Entries.Count(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                                                     && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
                return new Stats(CountWords(text), CountCharacters(text), slides, "stats.slides", "stats.slidesShort", "stats.slideOne");
            }
            case OfficeFamily.Excel:
            {
                var text = SheetPlainText(zip);
                var sheets = Sheets(zip).Count();
                return new Stats(CountWords(text), CountCharacters(text), sheets, "stats.sheets", "stats.sheetsShort", "stats.sheetOne");
            }
            default:
                throw new UserFacingException(Loc.T("reason.unsupported"));
        }
    }

    static string WordPlainText(ZipArchive zip)
    {
        if (zip.GetEntry("word/document.xml") is not { } entry) throw new UserFacingException(Loc.T("error.office"));
        var doc = XDocument.Parse(ReadText(entry));
        return string.Join(Environment.NewLine, doc.Descendants(W + "p").Select(ParagraphText));
    }

    static string SlidePlainText(ZipArchive zip)
    {
        var sb = new StringBuilder();
        foreach (var slide in zip.Entries
                     .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                                 && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => NumberIn(e.FullName)))
        {
            var doc = XDocument.Parse(ReadText(slide));
            foreach (var run in doc.Descendants(A + "t")) sb.AppendLine(run.Value);
        }
        return sb.ToString();
    }

    static string SheetPlainText(ZipArchive zip)
    {
        var shared = SharedStrings(zip);
        var sb = new StringBuilder();
        foreach (var (_, path) in Sheets(zip))
        {
            if (zip.GetEntry(path) is not { } entry) continue;
            foreach (var cell in XDocument.Parse(ReadText(entry)).Descendants(S + "c"))
            {
                sb.AppendLine(CellText(cell, shared));
            }
        }
        return sb.ToString();
    }

    static int PageCount(ZipArchive zip)
    {
        if (zip.GetEntry(AppPart) is not { } entry) return 0;
        var pages = XDocument.Parse(ReadText(entry)).Root?.Element(Ap + "Pages")?.Value;
        return int.TryParse(pages, out var n) ? n : 0;
    }

    static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    static int CountCharacters(string text) => text.Count(c => !char.IsWhiteSpace(c));

    // ------------------------------------------------------------------ compress media

    /// <summary>
    /// Re-encodes the large pictures embedded in a document to make the file smaller, keeping their
    /// names, formats and content types (so the package stays valid), and writes the result beside
    /// the source. If nothing got smaller the copy is removed and the job reports "already optimal"
    /// rather than handing back a file identical to the one that was dragged.
    /// </summary>
    public static string CompressMedia(string source, JobContext ctx)
    {
        Guard(source);
        var output = OutputPaths.For(source, FileKinds.FormatToken(source), "suffix.compressed");
        File.Copy(source, output, overwrite: false);
        long saved = 0;
        try
        {
            using (var zip = ZipFile.Open(output, ZipArchiveMode.Update))
            {
                var media = zip.Entries
                    .Where(e => e.Length >= WorthCompressing && IsPicture(e.Name))
                    .OrderByDescending(e => e.Length)
                    .ToList();
                for (var i = 0; i < media.Count; i++)
                {
                    ctx.Token.ThrowIfCancellationRequested();
                    var entry = media[i];
                    var original = ReadBytes(entry);
                    var smaller = Reencode(original, entry.Name, ctx.Settings.JpegQuality);
                    if (smaller is not null && smaller.Length < original.Length)
                    {
                        WriteEntry(zip, entry.FullName, smaller);
                        saved += original.Length - smaller.Length;
                    }
                    ctx.Report(0.9 * (i + 1) / media.Count);
                }
            }
        }
        catch
        {
            Ffmpeg.TryDelete(output);
            throw;
        }

        if (saved == 0)
        {
            Ffmpeg.TryDelete(output);
            throw new UnchangedException("result.alreadyOptimal");
        }
        return output;
    }

    static bool IsPicture(string name) =>
        name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Re-encodes one picture at the user's JPEG quality. PNGs keep their format and are quantised
    /// to a 256-colour palette, which only makes sense when they have no transparency; anything
    /// unusual (a decoder we do not have, an exotic format) is left exactly as it was.
    /// </summary>
    static byte[]? Reencode(byte[] data, string name, int quality)
    {
        try
        {
            using var input = new MemoryStream(data);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            BitmapSource source = frame;
            BitmapEncoder encoder;
            if (IsJpeg(name))
            {
                encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 40, 100) };
            }
            else
            {
                if (HasAlpha(frame)) return null;   // transparency cannot survive quantisation
                source = new FormatConvertedBitmap(frame, PixelFormats.Indexed8, new BitmapPalette(frame, 256), 0);
                encoder = new PngBitmapEncoder();
            }

            encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    static bool IsJpeg(string name) =>
        name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    static bool HasAlpha(BitmapSource source) =>
        source.Format is var format && (format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32
                                        || format == PixelFormats.Rgba64 || format == PixelFormats.Prgba64
                                        || format == PixelFormats.Bgr101010);

    // ------------------------------------------------------------------ zip plumbing

    /// <summary>
    /// Opens the package for reading, turning the two cases a user can actually hit into clear
    /// messages: an encrypted document is an OLE file with a .docx name, and a truncated or renamed
    /// file is not a zip at all.
    /// </summary>
    static ZipArchive Open(string source)
    {
        Guard(source);
        try
        {
            return ZipFile.OpenRead(source);
        }
        catch (InvalidDataException)
        {
            throw new UserFacingException(Loc.T("error.notOpenXml"));
        }
    }

    static void Guard(string source)
    {
        if (OfficeOps.IsEncrypted(source)) throw new UserFacingException(Loc.T("error.passwordProtected"));
    }

    static string ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Empties a properties part, reporting whether it actually held anything. Rewriting an already
    /// empty part is not a change, and the caller must be able to say "nothing to remove".
    /// </summary>
    static bool Clear(ZipArchive zip, string name, Func<XDocument> empty)
    {
        if (zip.GetEntry(name) is not { } entry) return false;
        var had = XDocument.Parse(ReadText(entry)).Root?.HasElements == true;
        Write(zip, name, empty());
        return had;
    }

    static void Write(ZipArchive zip, string name, XDocument xml)
    {
        zip.GetEntry(name)?.Delete();
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        xml.Save(writer, SaveOptions.DisableFormatting);
    }

    static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        zip.GetEntry(name)?.Delete();
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    static bool Drop(ZipArchive zip, string name)
    {
        if (zip.GetEntry(name) is not { } entry) return false;
        entry.Delete();
        return true;
    }
}
