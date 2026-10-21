using System.Text.RegularExpressions;
using DragDone.Core;

namespace DragDone.Processors;

/// <summary>Video and audio through a local FFmpeg.</summary>
public static partial class MediaOps
{
    static readonly string[] H264 = { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p" };

    static readonly HashSet<string> AudioOnly = new() { "mp3", "m4a", "wav", "flac", "ogg", "opus", "aac" };

    public static string Convert(string source, string token, JobContext ctx)
    {
        var output = OutputPaths.For(source, token);
        var args = new List<string> { "-i", source };
        args.AddRange(token switch
        {
            "mp4" or "mov" => H264.Concat(new[] { "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart" }),
            "webm" => new[] { "-c:v", "libvpx-vp9", "-crf", "33", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4", "-c:a", "libopus", "-b:a", "128k" },
            "gif" => new[]
            {
                "-filter_complex",
                "[0:v]fps=12,scale='min(640,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4",
                "-loop", "0",
            },
            _ when AudioOnly.Contains(token) => new[] { "-vn", "-map", "0:a:0?" }.Concat(AudioCodec(token, null)),
            _ => throw new UserFacingException(Localization.Loc.T("reason.unsupported")),
        });
        if (token is "mp4" or "mov" or "webm") args.InsertRange(2, new[] { "-map", "0:v:0", "-map", "0:a:0?" });
        args.Add(output);
        Ffmpeg.Run(ctx, args, output);
        return output;
    }

    /// <summary>Encoder arguments for an audio container. <paramref name="bitrate"/> overrides the default.</summary>
    static string[] AudioCodec(string token, string? bitrate) => token switch
    {
        "mp3" => bitrate is null ? new[] { "-c:a", "libmp3lame", "-q:a", "2" } : new[] { "-c:a", "libmp3lame", "-b:a", bitrate },
        "m4a" or "aac" => new[] { "-c:a", "aac", "-b:a", bitrate ?? "192k" },
        "wav" => new[] { "-c:a", "pcm_s16le" },
        "flac" => new[] { "-c:a", "flac" },
        "ogg" => bitrate is null ? new[] { "-c:a", "libvorbis", "-q:a", "5" } : new[] { "-c:a", "libvorbis", "-b:a", bitrate },
        "opus" => new[] { "-c:a", "libopus", "-b:a", bitrate ?? "128k" },
        _ => new[] { "-c:a", "aac", "-b:a", bitrate ?? "192k" },
    };

    [GeneratedRegex(@"Audio:\s*([a-z0-9_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AudioCodecRx();

    [GeneratedRegex(@"Stream #\d+:\d+.*?: Video:")]
    private static partial Regex VideoStreamRx();

    public static string ExtractAudio(string source, JobContext ctx)
    {
        var info = Ffmpeg.Probe(ctx, source);
        var m = AudioCodecRx().Match(info);
        if (!m.Success) throw new UserFacingException(Localization.Loc.T("error.noAudio"));
        // Copy the stream when a matching container exists: instant and lossless.
        var (ext, copy) = m.Groups[1].Value.ToLowerInvariant() switch
        {
            "aac" => ("m4a", true),
            "mp3" => ("mp3", true),
            "opus" => ("opus", true),
            "vorbis" => ("ogg", true),
            "flac" => ("flac", true),
            _ => ("m4a", false),
        };
        var output = OutputPaths.For(source, ext, "suffix.audio");
        var args = new List<string> { "-i", source, "-vn", "-map", "0:a:0" };
        args.AddRange(copy ? new[] { "-c:a", "copy" } : AudioCodec("m4a", "192k"));
        args.Add(output);
        Ffmpeg.Run(ctx, args, output);
        return output;
    }

    public static string Mute(string source, JobContext ctx)
    {
        var output = OutputPaths.For(source, Path.GetExtension(source), "suffix.muted");
        Ffmpeg.Run(ctx, new[] { "-i", source, "-map", "0:v", "-c:v", "copy", "-an", output }, output);
        return output;
    }

    public static string Compress(string source, string level, JobContext ctx)
    {
        var (crf, audio) = level switch
        {
            "light" => ("24", "128k"),
            "strong" => ("32", "64k"),
            _ => ("28", "96k"),
        };
        var output = OutputPaths.For(source, "mp4", "suffix.compressed");
        Ffmpeg.Run(ctx, new[]
        {
            "-i", source, "-map", "0:v:0", "-map", "0:a:0?", "-c:v", "libx264", "-preset", "medium", "-crf", crf, "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", audio, "-movflags", "+faststart", output,
        }, output);
        return ImageOps.KeepIfSmaller(source, output);
    }

    public static string Resize(string source, int height, JobContext ctx)
    {
        var output = OutputPaths.For(source, "mp4", null, $"{height}p");
        Ffmpeg.Run(ctx, new[]
        {
            "-i", source, "-map", "0:v:0", "-map", "0:a:0?", "-vf", $"scale=-2:'min({height},ih)'",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "copy", "-movflags", "+faststart", output,
        }, output);
        return output;
    }

    public static string Rotate(string source, int degrees, JobContext ctx)
    {
        var filter = degrees switch
        {
            90 => "transpose=1",
            270 => "transpose=2",
            _ => "hflip,vflip",
        };
        var output = OutputPaths.For(source, "mp4", "suffix.rotated");
        Ffmpeg.Run(ctx, new[]
        {
            "-i", source, "-map", "0:v:0", "-map", "0:a:0?", "-vf", filter, "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
            "-pix_fmt", "yuv420p", "-c:a", "copy", "-movflags", "+faststart", output,
        }, output);
        return output;
    }

    public static string Snapshot(string source, JobContext ctx)
    {
        var output = OutputPaths.For(source, "png", "suffix.frame");
        // Skip the first second (often black) when the clip is long enough; fall back to frame 0.
        try
        {
            Ffmpeg.Run(ctx, new[] { "-ss", "1", "-i", source, "-frames:v", "1", "-update", "1", output }, output);
        }
        catch (UserFacingException)
        {
            Ffmpeg.Run(ctx, new[] { "-i", source, "-frames:v", "1", "-update", "1", output }, output);
        }
        if (!File.Exists(output) || new FileInfo(output).Length == 0)
        {
            Ffmpeg.TryDelete(output);
            Ffmpeg.Run(ctx, new[] { "-i", source, "-frames:v", "1", "-update", "1", output }, output);
        }
        return output;
    }

    // ------------------------------------------------------------------ audio tools

    static string SameAudioToken(string source)
    {
        var token = FileKinds.FormatToken(source);
        return AudioOnly.Contains(token) ? token : "m4a";
    }

    public static string CompressAudio(string source, JobContext ctx)
    {
        var token = SameAudioToken(source);
        // Lossless sources become AAC: re-encoding WAV to WAV cannot shrink it.
        if (token is "wav" or "flac") token = "m4a";
        var output = OutputPaths.For(source, token, "suffix.compressed");
        var args = new List<string> { "-i", source, "-vn", "-map", "0:a:0" };
        args.AddRange(AudioCodec(token, token == "opus" ? "64k" : "96k"));
        args.Add(output);
        Ffmpeg.Run(ctx, args, output);
        return ImageOps.KeepIfSmaller(source, output);
    }

    static string AudioFilter(string source, string filter, string suffixKey, JobContext ctx, params string[] extra)
    {
        var token = SameAudioToken(source);
        var output = OutputPaths.For(source, token, suffixKey);
        var args = new List<string> { "-i", source, "-vn", "-map", "0:a:0" };
        if (filter.Length > 0) args.AddRange(new[] { "-af", filter });
        args.AddRange(extra);
        args.AddRange(AudioCodec(token, null));
        args.Add(output);
        Ffmpeg.Run(ctx, args, output);
        return output;
    }

    // loudnorm upsamples to 192 kHz internally; resample back so the file does not balloon.
    public static string Normalize(string source, JobContext ctx) =>
        AudioFilter(source, "loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000", "suffix.normalized", ctx);

    public static string TrimSilence(string source, JobContext ctx) =>
        AudioFilter(source,
            "silenceremove=start_periods=1:start_silence=0.1:start_threshold=-50dB,areverse," +
            "silenceremove=start_periods=1:start_silence=0.1:start_threshold=-50dB,areverse",
            "suffix.trimmed", ctx);

    public static string Mono(string source, JobContext ctx) => AudioFilter(source, "", "suffix.mono", ctx, "-ac", "1");

    public static bool HasVideo(JobContext ctx, string source) => VideoStreamRx().IsMatch(Ffmpeg.Probe(ctx, source));
}
