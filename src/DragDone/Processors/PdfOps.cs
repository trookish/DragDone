using System.Windows.Media.Imaging;
using DragDone.Core;
using DragDone.Localization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using WinPdf = Windows.Data.Pdf;

namespace DragDone.Processors;

/// <summary>
/// PDF assembly with PdfSharp (merge, split, rotate, compress, image → PDF) and page rendering
/// with Windows.Data.Pdf, the rasteriser built into Windows 10/11.
/// </summary>
public static class PdfOps
{
    const double LongEdgePt = 842; // A4 long edge

    // ------------------------------------------------------------------ images → PDF

    public static string ImagesToPdf(IReadOnlyList<string> images, JobContext ctx)
    {
        var output = images.Count == 1
            ? OutputPaths.For(images[0], "pdf")
            : OutputPaths.For(images[0], "pdf", "suffix.merged");
        var bitmaps = new List<BitmapSource>();
        for (var i = 0; i < images.Count; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            bitmaps.Add(ImageOps.Load(images[i], ctx));
            ctx.ReportOverall(0.5 * (i + 1) / images.Count);
        }
        ImagesToPdf(bitmaps, output, ctx);
        return output;
    }

    public static void ImagesToPdf(IReadOnlyList<BitmapSource> bitmaps, string output, JobContext ctx)
    {
        using var doc = new PdfDocument();
        doc.Info.Creator = "DragDone";
        var temps = new List<string>();
        try
        {
            foreach (var bmp in bitmaps)
            {
                ctx.Token.ThrowIfCancellationRequested();
                // JPEG for photos, PNG when transparency must survive.
                var alpha = ImageOps.HasAlpha(bmp);
                var tmp = Path.Combine(Path.GetTempPath(), $"dragdone-{Guid.NewGuid():N}.{(alpha ? "png" : "jpg")}");
                temps.Add(tmp);
                ImageOps.Save(bmp, tmp, alpha ? "png" : "jpg", ctx, 92);

                var scale = LongEdgePt / Math.Max(bmp.PixelWidth, bmp.PixelHeight);
                double w = bmp.PixelWidth * scale, h = bmp.PixelHeight * scale;
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(w);
                page.Height = XUnit.FromPoint(h);
                using var gfx = XGraphics.FromPdfPage(page);
                using var img = XImage.FromFile(tmp);
                gfx.DrawImage(img, 0, 0, w, h);
            }
            SaveAtomic(doc, output);
        }
        finally
        {
            foreach (var t in temps) Ffmpeg.TryDelete(t);
        }
    }

    // ------------------------------------------------------------------ assembly

    static PdfDocument Open(string path, PdfDocumentOpenMode mode)
    {
        try
        {
            return PdfReader.Open(path, mode);
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            throw new UserFacingException(Loc.T("error.pdfOpen"));
        }
    }

    static void SaveAtomic(PdfDocument doc, string output)
    {
        var tmp = output + ".dragdone-tmp";
        try
        {
            doc.Save(tmp);
            File.Move(tmp, output, overwrite: false);
        }
        finally
        {
            Ffmpeg.TryDelete(tmp);
        }
    }

    public static string Merge(IReadOnlyList<string> pdfs, JobContext ctx)
    {
        var output = OutputPaths.For(pdfs[0], "pdf", "suffix.merged");
        using var target = new PdfDocument();
        target.Info.Creator = "DragDone";
        for (var i = 0; i < pdfs.Count; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            using var src = Open(pdfs[i], PdfDocumentOpenMode.Import);
            foreach (var page in src.Pages) target.AddPage(page);
            ctx.ReportOverall((i + 1.0) / pdfs.Count);
        }
        SaveAtomic(target, output);
        return output;
    }

    public static IReadOnlyList<string> Split(string pdf, JobContext ctx)
    {
        using var src = Open(pdf, PdfDocumentOpenMode.Import);
        if (src.PageCount < 2) throw new UnchangedException("result.singlePage");
        var folder = OutputPaths.FolderNear(pdf, "suffix.pages");
        Directory.CreateDirectory(folder);
        var stem = Path.GetFileNameWithoutExtension(pdf);
        var digits = src.PageCount.ToString().Length;
        for (var i = 0; i < src.PageCount; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            using var one = new PdfDocument();
            one.AddPage(src.Pages[i]);
            one.Save(Path.Combine(folder, $"{stem} - {(i + 1).ToString().PadLeft(digits, '0')}.pdf"));
            ctx.Report((i + 1.0) / src.PageCount);
        }
        return new[] { folder };
    }

    public static string Rotate(string pdf, int degrees, JobContext ctx)
    {
        using var doc = Open(pdf, PdfDocumentOpenMode.Modify);
        foreach (var page in doc.Pages) page.Rotate = ((page.Rotate + degrees) % 360 + 360) % 360;
        var output = OutputPaths.For(pdf, "pdf", "suffix.rotated");
        SaveAtomic(doc, output);
        return output;
    }

    public static string Compress(string pdf, JobContext ctx)
    {
        using var doc = Open(pdf, PdfDocumentOpenMode.Modify);
        doc.Options.NoCompression = false;
        doc.Options.CompressContentStreams = true;
        doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        doc.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Automatic;
        doc.Options.EnableCcittCompressionForBilevelImages = true;
        var output = OutputPaths.For(pdf, "pdf", "suffix.compressed");
        SaveAtomic(doc, output);
        return ImageOps.KeepIfSmaller(pdf, output);
    }

    public static string StripMetadata(string pdf, JobContext ctx)
    {
        using var doc = Open(pdf, PdfDocumentOpenMode.Modify);
        doc.Info.Elements.Clear();
        doc.Internals.Catalog.Elements.Remove("/Metadata");
        var output = OutputPaths.For(pdf, "pdf", "suffix.clean");
        SaveAtomic(doc, output);
        return output;
    }

    public static int PageCount(string pdf)
    {
        using var doc = Open(pdf, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    // ------------------------------------------------------------------ PDF → images

    /// <summary>Renders every page. One page → a file beside the PDF; more → a folder of pages.</summary>
    public static IReadOnlyList<string> ToImages(string pdf, string token, JobContext ctx)
    {
        // Windows.Data.Pdf is agile; run it off the STA so the awaits cannot deadlock.
        return Task.Run(() => ToImagesAsync(pdf, token, ctx)).GetAwaiter().GetResult();
    }

    static async Task<IReadOnlyList<string>> ToImagesAsync(string pdf, string token, JobContext ctx)
    {
        WinPdf.PdfDocument doc;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdf));
            doc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new UserFacingException(Loc.T("error.pdfOpen"));
        }

        var count = (int)doc.PageCount;
        string? folder = null;
        string Target(int index)
        {
            if (count == 1) return OutputPaths.For(pdf, token);
            return Path.Combine(folder!, $"{Path.GetFileNameWithoutExtension(pdf)} - {(index + 1).ToString().PadLeft(count.ToString().Length, '0')}.{token}");
        }

        var outputs = new List<string>();
        if (count > 1)
        {
            folder = OutputPaths.FolderNear(pdf, "suffix.pages");
            Directory.CreateDirectory(folder);
            outputs.Add(folder);
        }

        for (uint i = 0; i < doc.PageCount; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            using var page = doc.GetPage(i);
            var options = new WinPdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * 2.0)), // ~192 DPI
            };
            if (token == "jpg") options.BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId;
            using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(ras, options);
            var target = Target((int)i);
            await using (var fs = File.Create(target))
            {
                await ras.AsStreamForRead().CopyToAsync(fs, ctx.Token);
            }
            if (count == 1) outputs.Add(target);
            ctx.Report((i + 1.0) / count);
        }
        return outputs;
    }
}
