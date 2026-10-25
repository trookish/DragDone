using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DragDone.Core;
using DragDone.Localization;

namespace DragDone.Processors;

/// <summary>
/// Documents. Plain-text formats are laid out by WPF (full Arabic shaping and bidi) and embedded
/// as page images; Office formats are delegated to <see cref="OfficeOps"/>, which drives the
/// application that owns them. Must run on an STA thread.
/// </summary>
public static class DocumentOps
{
    public static bool OfficeAvailable => OfficeOps.AnyInstalled;

    public static string ToPdf(string source, JobContext ctx) => Convert(source, "pdf", ctx);

    /// <summary>
    /// Converts one document to <paramref name="token"/>. An Office document goes through its own
    /// application; plain text can only become a PDF, because that is the only thing we can render.
    /// </summary>
    public static string Convert(string source, string token, JobContext ctx)
    {
        if (FileKinds.Office(source) != OfficeFamily.None) return OfficeOps.Convert(source, token, ctx);
        if (token == "pdf" && FileKinds.IsPlainText(source)) return TextToPdf(source, ctx);
        throw new UserFacingException(Loc.T("reason.unsupported"));
    }

    // ------------------------------------------------------------------ plain text

    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // A byte-order mark names the encoding outright: Notepad's "Unicode" is UTF-16LE.
        if (bytes is [0xFF, 0xFE, 0, 0, ..]) return new UTF32Encoding(false, true).GetString(bytes, 4, bytes.Length - 4);
        if (bytes is [0, 0, 0xFE, 0xFF, ..]) return new UTF32Encoding(true, true).GetString(bytes, 4, bytes.Length - 4);
        if (bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        // UTF-16 without a mark still gives itself away: in Latin text every other byte is zero.
        if (LooksLikeUtf16(bytes, out var bigEndian))
            return (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(bytes);
        try
        {
            // Strict UTF-8 next (with or without BOM), then Windows Arabic for legacy files.
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1256).GetString(bytes);
        }
    }

    static bool LooksLikeUtf16(byte[] bytes, out bool bigEndian)
    {
        bigEndian = false;
        var n = Math.Min(bytes.Length, 4096) & ~1;
        if (n < 4) return false;
        int evenZero = 0, oddZero = 0;
        for (var k = 0; k < n; k += 2)
        {
            if (bytes[k] == 0) evenZero++;
            if (bytes[k + 1] == 0) oddZero++;
        }
        var pairs = n / 2;
        // Real UTF-8 or ANSI text has no NUL bytes; require a strong, one-sided pattern.
        if (oddZero > pairs * 0.4 && evenZero < pairs * 0.05) return true;
        if (evenZero > pairs * 0.4 && oddZero < pairs * 0.05) { bigEndian = true; return true; }
        return false;
    }

    public static string TextToPdf(string source, JobContext ctx)
    {
        var text = ReadText(source).Replace("\r\n", "\n");
        var markdown = Path.GetExtension(source).ToLowerInvariant() is ".md" or ".markdown";
        var code = !markdown && Path.GetExtension(source).ToLowerInvariant() is not ".txt" and not ".log";

        const double dpi = 150;
        const double pageW = 793.7, pageH = 1122.5; // A4 in DIPs (96/inch)
        var doc = new FlowDocument
        {
            PageWidth = pageW,
            PageHeight = pageH,
            PagePadding = new Thickness(72),
            ColumnWidth = double.PositiveInfinity,
            FontFamily = new FontFamily(code ? "Cascadia Mono, Consolas, Courier New" : "Segoe UI, Tahoma"),
            FontSize = code ? 11 : 12.5,
            TextAlignment = TextAlignment.Left,
        };

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            var para = new Paragraph { Margin = new Thickness(0, 0, 0, code ? 0 : 4) };
            if (markdown)
            {
                var level = line.TakeWhile(c => c == '#').Count();
                if (level is > 0 and <= 6 && line.Length > level && line[level] == ' ')
                {
                    line = line[(level + 1)..];
                    para.FontSize = 24 - level * 2.5;
                    para.FontWeight = FontWeights.SemiBold;
                    para.Margin = new Thickness(0, 10, 0, 6);
                }
                else if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    line = "•  " + line[2..];
                }
                line = line.Replace("**", "").Replace("`", "");
            }
            // Paragraph direction follows its first strong character, like a text editor.
            var rtl = FirstStrongIsRtl(line);
            para.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            para.TextAlignment = TextAlignment.Left; // Left in an RTL paragraph = start edge
            para.Inlines.Add(new Run(line.Length == 0 ? "\u00A0" : line));
            doc.Blocks.Add(para);
        }

        var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
        paginator.PageSize = new Size(pageW, pageH);
        paginator.ComputePageCount();
        var pages = new List<BitmapSource>();
        for (var i = 0; i < paginator.PageCount; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            using var page = paginator.GetPage(i);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, pageW, pageH));
                dc.DrawRectangle(new VisualBrush(page.Visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(0, 0, pageW, pageH));
            }
            var rtb = new RenderTargetBitmap((int)(pageW * dpi / 96), (int)(pageH * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var gray = new FormatConvertedBitmap(rtb, PixelFormats.Gray8, null, 0);
            var frozen = new WriteableBitmap(gray);
            frozen.Freeze();
            pages.Add(frozen);
            ctx.Report(0.8 * (i + 1) / paginator.PageCount);
        }
        if (pages.Count == 0) throw new UnchangedException("result.emptyDocument");

        var output = OutputPaths.For(source, "pdf");
        PdfOps.ImagesToPdf(pages, output, ctx);
        return output;
    }

    public static bool FirstStrongIsRtl(string s)
    {
        foreach (var c in s)
        {
            if (c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFEFC') return true;
            if (char.IsLetter(c)) return false;
        }
        return false;
    }

    // ------------------------------------------------------------------ Office

}
