using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DragDone.Core;
using DragDone.Localization;

namespace DragDone.Processors;

/// <summary>Locates and runs a local FFmpeg. DragDone never downloads anything.</summary>
public static partial class Ffmpeg
{
    static string? _cached;
    static string? _cachedFor;

    public static string? Locate(string? configured)
    {
        if (_cachedFor == (configured ?? "") && _cached is not null && File.Exists(_cached)) return _cached;
        _cachedFor = configured ?? "";
        _cached = Candidates(configured).FirstOrDefault(File.Exists);
        return _cached;
    }

    public static void ResetCache()
    {
        _cached = null;
        _cachedFor = null;
    }

    static IEnumerable<string> Candidates(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return Directory.Exists(configured) ? Path.Combine(configured, "ffmpeg.exe") : configured!;
        }
        yield return Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe");
            }
            catch (ArgumentException)
            {
                continue;
            }
            yield return candidate;
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var pkg in SafeDirs(packages, "*FFmpeg*"))
            foreach (var build in SafeDirs(pkg, "*"))
                yield return Path.Combine(build, "bin", "ffmpeg.exe");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, "scoop", "shims", "ffmpeg.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", "ffmpeg.exe");
        yield return @"C:\ffmpeg\bin\ffmpeg.exe";
        yield return @"C:\Program Files\ffmpeg\bin\ffmpeg.exe";
    }

    static IEnumerable<string> SafeDirs(string root, string pattern)
    {
        try
        {
            return Directory.GetDirectories(root, pattern);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    public static string Require(JobContext ctx) =>
        Locate(ctx.Settings.FfmpegPath) ?? throw new UserFacingException(Loc.T("reason.needsFfmpeg"));

    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationRx();

    /// <summary>
    /// Runs FFmpeg with <paramref name="args"/> (input/output included). Reports progress from
    /// <c>-progress pipe:1</c>. Deletes <paramref name="output"/> on failure or cancellation.
    /// Returns the full stderr text.
    /// </summary>
    public static string Run(JobContext ctx, IReadOnlyList<string> args, string? output)
    {
        var exe = Require(ctx);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats" }) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        double duration = 0;
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr) stderr.AppendLine(e.Data);
            if (duration <= 0)
            {
                var m = DurationRx().Match(e.Data);
                if (m.Success)
                {
                    duration = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 +
                               double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                }
            }
        };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null || duration <= 0) return;
            if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(e.Data.AsSpan(12), out var us) && us > 0)
            {
                ctx.Report(us / 1_000_000.0 / duration);
            }
        };

        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        using (ctx.Token.Register(() =>
               {
                   try
                   {
                       if (!proc.HasExited) proc.Kill(true);
                   }
                   catch (InvalidOperationException) { }
               }))
        {
            proc.WaitForExit();
        }

        string text;
        lock (stderr) text = stderr.ToString();
        if (ctx.Token.IsCancellationRequested)
        {
            TryDelete(output);
            throw new OperationCanceledException(ctx.Token);
        }
        if (proc.ExitCode != 0)
        {
            TryDelete(output);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var tail = string.Join(" ", lines.TakeLast(2));
            throw new UserFacingException(Loc.T("error.ffmpeg") + (tail.Length > 0 ? " — " + tail : ""));
        }
        ctx.Report(1);
        return text;
    }

    /// <summary>Stream summary from <c>ffmpeg -i</c> (exit code 1 is expected: no output given).</summary>
    public static string Probe(JobContext ctx, string input)
    {
        var exe = Require(ctx);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(input);
        using var p = Process.Start(psi)!;
        var text = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return text;
    }

    public static void TryDelete(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
