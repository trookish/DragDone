using System.Diagnostics;
using System.Text;
using DragDone.Localization;

namespace DragDone.Core;

/// <summary>
/// An action the user adds themselves: a program run on the dropped files, shown in the ring of
/// their choice for the file types they list. A conversion is a custom action with an output
/// format; DragDone picks the output name, so originals are never overwritten.
/// </summary>
public sealed class CustomAction
{
    /// <summary>Stable id used in the ring (custom:{Id}); survives renames and reordering.</summary>
    public string Id { get; set; } = NewId();

    public string Name { get; set; } = "";

    /// <summary>Extensions and kinds the action accepts: ".png, jpg, video" or "*" for any file.</summary>
    public string Types { get; set; } = "*";

    /// <summary>Program to run: a full path, or a name found on PATH (magick, pandoc, 7z).</summary>
    public string Program { get; set; } = "";

    /// <summary>Command line with placeholders; see <see cref="CustomActions.Placeholders"/>.</summary>
    public string Arguments { get; set; } = "\"{file}\"";

    /// <summary>Output format (no dot) for conversions; empty for actions that write no new file.</summary>
    public string Output { get; set; } = "";

    public MenuTab Tab { get; set; } = MenuTab.Tools;
    public bool Enabled { get; set; } = true;

    /// <summary>Shows the program's console while it runs instead of hiding it.</summary>
    public bool ShowWindow { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    public string DisplayName => Name.Trim().Length > 0 ? Name.Trim()
        : Output.Trim().Length > 0 ? ActionCatalog.FormatLabel(OutputToken)
        : Path.GetFileNameWithoutExtension(Program.Trim().Trim('"'));

    public string OutputToken => Output.Trim().TrimStart('.').ToLowerInvariant();
    public bool IsConversion => OutputToken.Length > 0;
    public bool RunsOncePerDrop => Arguments.Contains("{files}", StringComparison.Ordinal);
}

/// <summary>Matching, command building and running for <see cref="CustomAction"/>.</summary>
public static class CustomActions
{
    public static readonly string[] Placeholders = { "{file}", "{files}", "{dir}", "{name}", "{ext}", "{out}" };

    static readonly Dictionary<string, FileKind> KindNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image"] = FileKind.Image, ["images"] = FileKind.Image,
        ["video"] = FileKind.Video, ["videos"] = FileKind.Video,
        ["audio"] = FileKind.Audio,
        ["pdf"] = FileKind.Pdf,
        ["document"] = FileKind.Document, ["documents"] = FileKind.Document,
        ["archive"] = FileKind.Archive, ["archives"] = FileKind.Archive,
        ["folder"] = FileKind.Folder, ["folders"] = FileKind.Folder,
    };

    /// <summary>The tokens of a Types string, normalised: extensions gain a dot, kinds stay words.</summary>
    public static IReadOnlyList<string> ParseTypes(string types) =>
        (types ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t == "*" || KindNames.ContainsKey(t) ? t.ToLowerInvariant() : "." + t.TrimStart('*').TrimStart('.').ToLowerInvariant())
            .Where(t => t != ".")
            .Distinct()
            .ToList();

    public static bool Accepts(CustomAction a, string path)
    {
        var kind = FileKinds.Of(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        foreach (var t in ParseTypes(a.Types))
        {
            if (t == "*") return kind != FileKind.Folder;
            if (KindNames.TryGetValue(t, out var k) && k == kind) return true;
            if (t.StartsWith('.') && t == ext && kind != FileKind.Folder) return true;
        }
        return false;
    }

    /// <summary>True when the action is complete enough to show and accepts every dropped path.</summary>
    public static bool Matches(CustomAction a, DragPayload p) =>
        a.Enabled && a.Program.Trim().Length > 0 && p.Count > 0 && p.Paths.All(x => Accepts(a, x));

    /// <summary>Why an action cannot be saved as written, as a catalogue key; null when it can.</summary>
    public static string? Problem(CustomAction a)
    {
        if (a.Program.Trim().Length == 0) return "custom.problem.program";
        if (ParseTypes(a.Types).Count == 0) return "custom.problem.types";
        if (a.Arguments.Contains("{out}") && !a.IsConversion) return "custom.problem.output";
        if (a.IsConversion && a.OutputToken.Any(c => !char.IsLetterOrDigit(c))) return "custom.problem.format";
        return null;
    }

    /// <summary>
    /// Fills the placeholders. Every substituted path is quoted unless the template already
    /// quotes it, so names with spaces reach the program as one argument.
    /// </summary>
    public static string Expand(string template, IReadOnlyList<string> files, string? output)
    {
        var file = files.Count > 0 ? files[0] : "";
        var values = new Dictionary<string, string>
        {
            ["{file}"] = file,
            ["{dir}"] = Path.GetDirectoryName(file) ?? "",
            ["{name}"] = Path.GetFileNameWithoutExtension(file),
            ["{ext}"] = Path.GetExtension(file).TrimStart('.'),
            ["{out}"] = output ?? "",
        };
        var sb = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '{')
            {
                var close = template.IndexOf('}', i);
                if (close > i)
                {
                    var token = template[i..(close + 1)];
                    var quoted = i > 0 && template[i - 1] == '"';
                    if (token == "{files}")
                    {
                        sb.Append(string.Join(" ", files.Select(f => Quote(f, quoted))));
                        i = close;
                        continue;
                    }
                    if (values.TryGetValue(token, out var value))
                    {
                        // Names and extensions are plain words; only paths need quoting.
                        sb.Append(token is "{name}" or "{ext}" ? value : Quote(value, quoted));
                        i = close;
                        continue;
                    }
                }
            }
            sb.Append(template[i]);
        }
        return sb.ToString();
    }

    static string Quote(string value, bool alreadyQuoted)
    {
        if (alreadyQuoted || value.Length == 0) return value;
        return value.Any(c => char.IsWhiteSpace(c) || c == '&' || c == '(' || c == ')') ? "\"" + value + "\"" : value;
    }

    /// <summary>Runs the action on the payload and reports what it produced.</summary>
    public static JobResult Run(CustomAction a, DragPayload p, JobContext ctx)
    {
        if (Problem(a) is { } problem) return JobResult.Fail(Loc.T(problem));
        if (a.RunsOncePerDrop)
        {
            ctx.BeginItem(0, 1);
            var output = a.IsConversion ? OutputPaths.For(p.Paths[0], a.OutputToken) : null;
            try { RunOnce(a, p.Paths, output, ctx); }
            catch (UserFacingException e) { return JobResult.Fail(e.Message); }
            ctx.ReportOverall(1);
            return Finish(a, output is null ? Array.Empty<string>() : new[] { output });
        }

        var outputs = new List<string>();
        var failures = new List<string>();
        for (var i = 0; i < p.Count; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            ctx.BeginItem(i, p.Count);
            var output = a.IsConversion ? OutputPaths.For(p.Paths[i], a.OutputToken) : null;
            try
            {
                RunOnce(a, new[] { p.Paths[i] }, output, ctx);
                if (output is not null) outputs.Add(output);
            }
            catch (UserFacingException e)
            {
                failures.Add($"{Path.GetFileName(p.Paths[i])}: {e.Message}");
            }
        }
        ctx.ReportOverall(1);
        if (failures.Count == p.Count) return JobResult.Fail(failures[0]);
        var result = Finish(a, outputs);
        if (failures.Count > 0)
            result = result with { Detail = Loc.F("result.partial", ("n", failures.Count)) + " — " + failures[0] };
        return result;
    }

    static JobResult Finish(CustomAction a, IReadOnlyList<string> outputs)
    {
        if (outputs.Count > 0) return JobResult.Created(outputs, "result.done");
        return new JobResult { Outcome = JobOutcome.Info, MessageKey = "result.done", Detail = a.DisplayName };
    }

    static void RunOnce(CustomAction a, IReadOnlyList<string> files, string? output, JobContext ctx)
    {
        var program = Environment.ExpandEnvironmentVariables(a.Program.Trim().Trim('"'));
        var info = new ProcessStartInfo(program, Expand(a.Arguments, files, output))
        {
            UseShellExecute = false,
            CreateNoWindow = !a.ShowWindow,
            RedirectStandardError = !a.ShowWindow,
            RedirectStandardOutput = !a.ShowWindow,
            WorkingDirectory = Path.GetDirectoryName(files[0]) ?? "",
        };

        Process? proc;
        try
        {
            proc = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new UserFacingException(Loc.F("custom.error.start", ("name", program)));
        }
        if (proc is null) throw new UserFacingException(Loc.F("custom.error.start", ("name", program)));

        using (proc)
        {
            var errors = new StringBuilder();
            if (!a.ShowWindow)
            {
                proc.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) lock (errors) errors.AppendLine(e.Data); };
                proc.OutputDataReceived += (_, _) => { };
                proc.BeginErrorReadLine();
                proc.BeginOutputReadLine();
            }
            var limit = Stopwatch.StartNew();
            while (!proc.WaitForExit(200))
            {
                if (ctx.Token.IsCancellationRequested || limit.Elapsed > TimeSpan.FromMinutes(30))
                {
                    try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    if (output is not null) TryDelete(output);
                    ctx.Token.ThrowIfCancellationRequested();
                    throw new UserFacingException(Loc.T("custom.error.timeout"));
                }
            }
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                if (output is not null) TryDelete(output);
                string last;
                lock (errors) last = errors.ToString().Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
                throw new UserFacingException(Loc.F("custom.error.exit", ("code", proc.ExitCode)) + (last.Length > 0 ? " — " + last : ""));
            }
        }
        if (output is not null && !File.Exists(output))
            throw new UserFacingException(Loc.T("custom.error.noOutput"));
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Ready-made examples the user can add with one click and then adjust.</summary>
    public static IReadOnlyList<CustomAction> Examples() => new[]
    {
        new CustomAction { Name = Loc.T("custom.example.notepad"), Types = "document", Program = "notepad.exe", Arguments = "{file}", Tab = MenuTab.Actions },
        new CustomAction { Name = "", Types = "image", Program = "magick", Arguments = "{file} {out}", Output = "jxl", Tab = MenuTab.Convert },
        new CustomAction { Name = "", Types = ".md, .markdown", Program = "pandoc", Arguments = "{file} -o {out}", Output = "docx", Tab = MenuTab.Convert },
        new CustomAction { Name = Loc.T("custom.example.extract"), Types = "archive", Program = "tar", Arguments = "-xf {file}", Tab = MenuTab.Tools },
    };
}
