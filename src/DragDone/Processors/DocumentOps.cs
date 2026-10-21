using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DragDone.Core;
using DragDone.Localization;

namespace DragDone.Processors;

/// <summary>
/// Document → PDF. Plain-text formats are laid out by WPF (full Arabic shaping and bidi) and
/// embedded as page images; Office formats are exported by the installed Office, if any.
/// Must run on an STA thread.
/// </summary>
public static class DocumentOps
{
    public static bool OfficeAvailable =>
        Type.GetTypeFromProgID("Word.Application") is not null ||
        Type.GetTypeFromProgID("Excel.Application") is not null ||
        Type.GetTypeFromProgID("PowerPoint.Application") is not null;

    public static string ToPdf(string source, JobContext ctx)
    {
        if (FileKinds.IsOfficeDocument(source)) return OfficeToPdf(source, ctx);
        if (FileKinds.IsPlainText(source)) return TextToPdf(source, ctx);
        throw new UserFacingException(Loc.T("reason.unsupported"));
    }

    // ------------------------------------------------------------------ plain text

    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            // Strict UTF-8 first (with or without BOM), then Windows Arabic for legacy files.
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1256).GetString(bytes);
        }
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

    public static string OfficeToPdf(string source, JobContext ctx)
    {
        var output = OutputPaths.For(source, "pdf");
        var full = Path.GetFullPath(source);
        var ext = Path.GetExtension(source).ToLowerInvariant();
        try
        {
            switch (ext)
            {
                case ".xls" or ".xlsx" or ".ods":
                    WithApp("Excel.Application", app =>
                    {
                        app.DisplayAlerts = false;
                        dynamic wb = app.Workbooks.Open(full, 0, true);
                        try { wb.ExportAsFixedFormat(0, output); }
                        finally { wb.Close(false); }
                    });
                    break;
                case ".ppt" or ".pptx" or ".odp":
                    WithApp("PowerPoint.Application", app =>
                    {
                        dynamic pres = app.Presentations.Open(full, -1, 0, 0); // ReadOnly, Untitled=false, WithWindow=false
                        try { pres.SaveAs(output, 32); }
                        finally { pres.Close(); }
                    });
                    break;
                default:
                    WithApp("Word.Application", app =>
                    {
                        app.DisplayAlerts = 0;
                        dynamic doc = app.Documents.Open(full, false, true, false);
                        try { doc.ExportAsFixedFormat(output, 17); }
                        finally { doc.Close(false); }
                    });
                    break;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                       or InvalidOperationException)
        {
            Ffmpeg.TryDelete(output);
            throw new UserFacingException(Loc.T("error.office"));
        }
        if (!File.Exists(output)) throw new UserFacingException(Loc.T("error.office"));
        return output;
    }

    static void WithApp(string progId, Action<dynamic> work)
    {
        var type = Type.GetTypeFromProgID(progId) ?? throw new UserFacingException(Loc.T("reason.needsOffice"));
        dynamic app = Activator.CreateInstance(type)!;
        try
        {
            if (progId != "PowerPoint.Application") app.Visible = false;
            work(app);
        }
        finally
        {
            try { app.Quit(); }
            catch (Exception) { /* Office already gone */ }
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(app);
        }
    }
}
