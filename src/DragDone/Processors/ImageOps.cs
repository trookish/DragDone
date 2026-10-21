using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DragDone.Core;
using DragDone.Localization;

namespace DragDone.Processors;

/// <summary>
/// Image work on WIC (built into Windows). WebP/AVIF encoding and decoding of formats WIC
/// lacks a codec for (HEIC/AVIF without the Store extensions) go through FFmpeg.
/// Must run on an STA thread (RenderTargetBitmap).
/// </summary>
public static class ImageOps
{
    /// <summary>Formats WIC can write without help.</summary>
    static readonly HashSet<string> WicWritable = new() { "png", "jpg", "bmp", "tiff", "gif" };

    // ------------------------------------------------------------------ load / save

    public static BitmapSource Load(string path, JobContext? ctx = null)
    {
        try
        {
            return LoadWic(path);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            if (ctx is null || Ffmpeg.Locate(ctx.Settings.FfmpegPath) is null)
                throw new UserFacingException(Loc.T("error.imageDecode"));
            var tmp = Path.Combine(Path.GetTempPath(), $"dragdone-{Guid.NewGuid():N}.png");
            try
            {
                Ffmpeg.Run(ctx, new[] { "-i", path, "-frames:v", "1", "-update", "1", tmp }, tmp);
                return LoadWic(tmp);
            }
            finally
            {
                Ffmpeg.TryDelete(tmp);
            }
        }
    }

    static BitmapSource LoadWic(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        // Icons: pick the largest frame.
        BitmapSource frame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
        var orientation = ReadOrientation(decoder.Frames[0]);
        BitmapSource src = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        src = ApplyOrientation(src, orientation);
        var copy = new WriteableBitmap(src); // detach from the stream and normalise DPI handling
        copy.Freeze();
        return copy;
    }

    static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata md && md.GetQuery("System.Photo.Orientation") is ushort o) return o;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
        }
        return 1;
    }

    static BitmapSource ApplyOrientation(BitmapSource src, int orientation) => orientation switch
    {
        2 => Transform(src, new ScaleTransform(-1, 1)),
        3 => Transform(src, new RotateTransform(180)),
        4 => Transform(src, new ScaleTransform(1, -1)),
        5 => Transform(Transform(src, new RotateTransform(90)), new ScaleTransform(-1, 1)),
        6 => Transform(src, new RotateTransform(90)),
        7 => Transform(Transform(src, new RotateTransform(270)), new ScaleTransform(-1, 1)),
        8 => Transform(src, new RotateTransform(270)),
        _ => src,
    };

    static BitmapSource Transform(BitmapSource src, Transform t)
    {
        var tb = new TransformedBitmap(src, t);
        tb.Freeze();
        return tb;
    }

    public static bool HasAlpha(BitmapSource src)
    {
        var bgra = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var row = new byte[stride];
        for (var y = 0; y < bgra.PixelHeight; y++)
        {
            bgra.CopyPixels(new Int32Rect(0, y, bgra.PixelWidth, 1), row, stride, 0);
            for (var i = 3; i < row.Length; i += 4)
            {
                if (row[i] != 255) return true;
            }
        }
        return false;
    }

    /// <summary>JPEG has no alpha: composite onto white.</summary>
    static BitmapSource Flatten(BitmapSource src)
    {
        var bgra = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);
        var outPx = new byte[w * 3 * h];
        for (int i = 0, o = 0; i < px.Length; i += 4, o += 3)
        {
            var a = px[i + 3] / 255.0;
            outPx[o] = (byte)Math.Round(px[i] * a + 255 * (1 - a));
            outPx[o + 1] = (byte)Math.Round(px[i + 1] * a + 255 * (1 - a));
            outPx[o + 2] = (byte)Math.Round(px[i + 2] * a + 255 * (1 - a));
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, outPx, w * 3);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Writes <paramref name="src"/> as <paramref name="token"/>. Returns the path written.</summary>
    public static void Save(BitmapSource src, string path, string token, JobContext ctx, int? quality = null)
    {
        var q = quality ?? ctx.Settings.JpegQuality;
        switch (token)
        {
            case "png":
            case "jpg":
            case "bmp":
            case "tiff":
            case "gif":
            {
                BitmapEncoder enc = token switch
                {
                    "png" => new PngBitmapEncoder(),
                    "jpg" => new JpegBitmapEncoder { QualityLevel = q },
                    "bmp" => new BmpBitmapEncoder(),
                    "tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
                    _ => new GifBitmapEncoder(),
                };
                var frame = token is "jpg" or "bmp" ? Flatten(src) : src;
                enc.Frames.Add(BitmapFrame.Create(frame));
                WriteAtomic(path, s => enc.Save(s));
                break;
            }
            case "ico":
                WriteAtomic(path, s => WriteIco(src, s));
                break;
            case "webp":
            case "avif":
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"dragdone-{Guid.NewGuid():N}.png");
                try
                {
                    Save(src, tmp, "png", ctx);
                    var args = token == "webp"
                        ? new List<string> { "-i", tmp, "-c:v", "libwebp", "-quality", q.ToString(), "-frames:v", "1", path }
                        : new List<string>
                        {
                            "-i", tmp, "-frames:v", "1", "-c:v", "libaom-av1", "-still-picture", "1", "-crf", (63 - q * 45 / 100).ToString(),
                            "-b:v", "0", "-cpu-used", "6", "-pix_fmt", "yuv420p", path,
                        };
                    Ffmpeg.Run(ctx, args, path);
                }
                finally
                {
                    Ffmpeg.TryDelete(tmp);
                }
                break;
            }
            case "pdf":
                PdfOps.ImagesToPdf(new[] { src }, path, ctx);
                break;
            default:
                throw new UserFacingException(Loc.T("reason.unsupported"));
        }
    }

    static void WriteAtomic(string path, Action<Stream> write)
    {
        var tmp = path + ".dragdone-tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write)) write(fs);
            File.Move(tmp, path, overwrite: false);
        }
        finally
        {
            Ffmpeg.TryDelete(tmp);
        }
    }

    static void WriteIco(BitmapSource src, Stream output)
    {
        var maxSide = Math.Max(src.PixelWidth, src.PixelHeight);
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 }.Where(s => s <= Math.Max(16, maxSide)).ToList();
        var images = sizes.Select(s =>
        {
            var square = FitSquare(src, s);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(square));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return (Size: s, Bytes: ms.ToArray());
        }).ToList();

        using var w = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, bytes) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(bytes.Length);
            w.Write(offset);
            offset += bytes.Length;
        }
        foreach (var (_, bytes) in images) w.Write(bytes);
    }

    /// <summary>Scales to fit a transparent square canvas.</summary>
    static BitmapSource FitSquare(BitmapSource src, int size)
    {
        var scale = Math.Min((double)size / src.PixelWidth, (double)size / src.PixelHeight);
        double w = src.PixelWidth * scale, h = src.PixelHeight * scale;
        return Render(size, size, dc => dc.DrawImage(src, new Rect((size - w) / 2, (size - h) / 2, w, h)));
    }

    static BitmapSource Render(int w, int h, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen()) draw(dc);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var bgra = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        var copy = new WriteableBitmap(bgra);
        copy.Freeze();
        return copy;
    }

    public static BitmapSource Scale(BitmapSource src, int w, int h) =>
        Render(Math.Max(1, w), Math.Max(1, h), dc => dc.DrawImage(src, new Rect(0, 0, w, h)));

    /// <summary>Output format for an edit (crop, rotate…): same as the source when we can write it.</summary>
    public static string EditFormat(string source, JobContext ctx)
    {
        var token = FileKinds.FormatToken(source);
        if (WicWritable.Contains(token)) return token;
        if (token is "webp" or "avif" && Ffmpeg.Locate(ctx.Settings.FfmpegPath) is not null) return token;
        return token is "heic" ? "jpg" : "png";
    }

    static string WriteEdit(string source, BitmapSource result, string suffixKey, JobContext ctx, int? quality = null)
    {
        var token = EditFormat(source, ctx);
        var output = OutputPaths.For(source, token, suffixKey);
        Save(result, output, token, ctx, quality ?? Math.Max(ctx.Settings.JpegQuality, 92));
        return output;
    }

    // ------------------------------------------------------------------ actions

    public static string Convert(string source, string token, JobContext ctx)
    {
        var img = Load(source, ctx);
        ctx.Report(0.4);
        var output = OutputPaths.For(source, token);
        Save(img, output, token, ctx);
        return output;
    }

    public static string Resize(string source, string arg, JobContext ctx)
    {
        var img = Load(source, ctx);
        int w, h;
        if (arg.EndsWith('%'))
        {
            var pct = int.Parse(arg.TrimEnd('%')) / 100.0;
            w = (int)Math.Round(img.PixelWidth * pct);
            h = (int)Math.Round(img.PixelHeight * pct);
        }
        else
        {
            var longEdge = int.Parse(arg);
            var current = Math.Max(img.PixelWidth, img.PixelHeight);
            if (current <= longEdge) throw new UnchangedException("result.alreadySmaller");
            var s = (double)longEdge / current;
            w = (int)Math.Round(img.PixelWidth * s);
            h = (int)Math.Round(img.PixelHeight * s);
        }
        var scaled = Scale(img, w, h);
        return WriteEdit(source, scaled, "suffix.resized", ctx, null);
    }

    public static string Rotate(string source, int degrees, JobContext ctx) =>
        WriteEdit(source, Transform(Load(source, ctx), new RotateTransform(degrees)), "suffix.rotated", ctx);

    public static string Flip(string source, bool horizontal, JobContext ctx) =>
        WriteEdit(source, Transform(Load(source, ctx), horizontal ? new ScaleTransform(-1, 1) : new ScaleTransform(1, -1)),
            "suffix.flipped", ctx);

    public static string CropAspect(string source, double ratio, JobContext ctx)
    {
        var img = Load(source, ctx);
        var rect = CenteredAspect(img.PixelWidth, img.PixelHeight, ratio);
        if (rect.Width == img.PixelWidth && rect.Height == img.PixelHeight) throw new UnchangedException("result.alreadyRatio");
        return Crop(source, img, rect, ctx);
    }

    public static Int32Rect CenteredAspect(int width, int height, double ratio)
    {
        int w = width, h = (int)Math.Round(width / ratio);
        if (h > height)
        {
            h = height;
            w = (int)Math.Round(height * ratio);
        }
        return new Int32Rect((width - w) / 2, (height - h) / 2, Math.Max(1, w), Math.Max(1, h));
    }

    public static string CropRect(string source, Int32Rect rect, JobContext ctx) => Crop(source, Load(source, ctx), rect, ctx);

    /// <summary>Crop by a selection given in fractions of the image (from the crop window).</summary>
    public static string CropFraction(string source, Rect fraction, JobContext ctx)
    {
        var img = Load(source, ctx);
        var rect = new Int32Rect(
            (int)Math.Round(fraction.X * img.PixelWidth),
            (int)Math.Round(fraction.Y * img.PixelHeight),
            (int)Math.Round(fraction.Width * img.PixelWidth),
            (int)Math.Round(fraction.Height * img.PixelHeight));
        return Crop(source, img, rect, ctx);
    }

    static string Crop(string source, BitmapSource img, Int32Rect rect, JobContext ctx)
    {
        rect = Clamp(rect, img.PixelWidth, img.PixelHeight);
        var cropped = new CroppedBitmap(img, rect);
        cropped.Freeze();
        return WriteEdit(source, cropped, "suffix.cropped", ctx);
    }

    static Int32Rect Clamp(Int32Rect r, int w, int h)
    {
        var x = Math.Clamp(r.X, 0, w - 1);
        var y = Math.Clamp(r.Y, 0, h - 1);
        return new Int32Rect(x, y, Math.Clamp(r.Width, 1, w - x), Math.Clamp(r.Height, 1, h - y));
    }

    public static string AutoTrim(string source, JobContext ctx)
    {
        var img = Load(source, ctx);
        var rect = TrimBounds(img, 24);
        if (rect is null) throw new UnchangedException("result.nothingToTrim");
        return Crop(source, img, rect.Value, ctx);
    }

    /// <summary>Bounding box of pixels that differ from the corner colour; null if nothing to trim.</summary>
    public static Int32Rect? TrimBounds(BitmapSource img, int tolerance)
    {
        int w = img.PixelWidth, h = img.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        img.CopyPixels(px, stride, 0);
        byte b0 = px[0], g0 = px[1], r0 = px[2], a0 = px[3];
        bool Differs(int x, int y)
        {
            var i = y * stride + x * 4;
            return Math.Abs(px[i] - b0) > tolerance || Math.Abs(px[i + 1] - g0) > tolerance ||
                   Math.Abs(px[i + 2] - r0) > tolerance || Math.Abs(px[i + 3] - a0) > tolerance;
        }

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (!Differs(x, y)) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        if (maxX < 0) return null; // uniform image
        var rect = new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        if (rect.Width == w && rect.Height == h) return null;
        return rect;
    }

    public static string StripMetadata(string source, JobContext ctx)
    {
        // Re-encoding through WIC writes pixels only; EXIF/XMP/GPS are not copied.
        var img = Load(source, ctx);
        return WriteEdit(source, img, "suffix.clean", ctx, 95);
    }

    public static string Compress(string source, JobContext ctx)
    {
        var token = FileKinds.FormatToken(source);
        var img = Load(source, ctx);
        ctx.Report(0.3);
        string output;
        switch (token)
        {
            case "jpg":
                output = OutputPaths.For(source, "jpg", "suffix.compressed");
                Save(img, output, "jpg", ctx, 72);
                break;
            case "png":
                output = OutputPaths.For(source, "png", "suffix.compressed");
                CompressPng(source, img, output, ctx);
                break;
            case "webp":
            case "avif":
                if (Ffmpeg.Locate(ctx.Settings.FfmpegPath) is null) throw new UserFacingException(Loc.T("reason.needsFfmpeg"));
                output = OutputPaths.For(source, token, "suffix.compressed");
                Save(img, output, token, ctx, 60);
                break;
            case "bmp":
            case "tiff":
            case "ico":
                // Uncompressed or lightly compressed: lossless PNG keeps every pixel.
                output = OutputPaths.For(source, "png", "suffix.compressed");
                Save(img, output, "png", ctx);
                break;
            default:
                // HEIC, GIF and the rest: a quality-75 JPEG is the practical small format.
                output = OutputPaths.For(source, "jpg", "suffix.compressed");
                Save(img, output, "jpg", ctx, 75);
                break;
        }
        return KeepIfSmaller(source, output);
    }

    static void CompressPng(string source, BitmapSource img, string output, JobContext ctx)
    {
        if (Ffmpeg.Locate(ctx.Settings.FfmpegPath) is not null)
        {
            // Palette quantisation that keeps transparency (pngquant-like result).
            Ffmpeg.Run(ctx, new[]
            {
                "-i", source, "-frames:v", "1", "-filter_complex",
                "[0:v]split[a][b];[a]palettegen=max_colors=256:reserve_transparent=1:stats_mode=full[p];[b][p]paletteuse=dither=sierra2_4a:alpha_threshold=128",
                "-compression_level", "9", output,
            }, output);
            return;
        }
        if (HasAlpha(img))
        {
            Save(img, output, "png", ctx);
            return;
        }
        var indexed = new FormatConvertedBitmap(img, PixelFormats.Indexed8, new BitmapPalette(img, 256), 0);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(indexed));
        WriteAtomic(output, s => enc.Save(s));
    }

    /// <summary>Deletes the output and reports "already optimal" when it is not smaller.</summary>
    public static string KeepIfSmaller(string source, string output)
    {
        if (File.Exists(output) && new FileInfo(output).Length >= new FileInfo(source).Length)
        {
            Ffmpeg.TryDelete(output);
            throw new UnchangedException("result.alreadyOptimal");
        }
        return output;
    }
}
