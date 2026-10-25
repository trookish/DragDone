using System.Windows;
using DragDone.Localization;
using DragDone.Processors;

namespace DragDone.Core;

/// <summary>
/// Runs a file-producing action for a payload. Interactive actions (share, shelf, terminal,
/// free crop, browse) are resolved by the UI first and never reach this class.
/// </summary>
public static class ActionExecutor
{
    public static bool IsInteractive(string id) =>
        id is "act.share" or "act.shelf" or "act.terminal" or "act.move:browse" or "img.crop:free" or "act.rename";

    /// <summary>Runs <paramref name="id"/> on a dedicated STA thread (WPF imaging and Office COM need one).</summary>
    public static Task<JobResult> RunAsync(string id, DragPayload p, JobContext ctx)
    {
        var tcs = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(Execute(id, p, ctx));
            }
            catch (Exception ex)
            {
                tcs.SetResult(JobResult.Fail(ex.Message));
            }
        })
        {
            IsBackground = true,
            Name = "DragDone job " + id,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public static JobResult Execute(string id, DragPayload p, JobContext ctx)
    {
        try
        {
            return Dispatch(id, p, ctx);
        }
        catch (OperationCanceledException)
        {
            return new JobResult { Outcome = JobOutcome.Cancelled, MessageKey = "result.cancelled" };
        }
        catch (UnchangedException u)
        {
            return new JobResult { Outcome = JobOutcome.Unchanged, MessageKey = u.MessageKey };
        }
        catch (UserFacingException e)
        {
            return JobResult.Fail(e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return JobResult.Fail(Loc.T("error.io") + " — " + e.Message);
        }
    }

    static (string Head, string Arg) SplitId(string id)
    {
        var i = id.IndexOf(':');
        return i < 0 ? (id, "") : (id[..i], id[(i + 1)..]);
    }

    static JobResult Dispatch(string id, DragPayload p, JobContext ctx)
    {
        var (head, arg) = SplitId(id);
        switch (head)
        {
            case "custom":
            {
                var action = ctx.Settings.CustomActions.FirstOrDefault(a => a.Id == arg);
                return action is null ? JobResult.Fail(Loc.T("custom.error.missing")) : CustomActions.Run(action, p, ctx);
            }

            case "convert":
                return p.Kind switch
                {
                    FileKind.Image => EachFile(p, ctx, f => ImageOps.Convert(f, arg, ctx)),
                    FileKind.Pdf => EachFileMany(p, ctx, f => PdfOps.ToImages(f, arg, ctx)),
                    FileKind.Video or FileKind.Audio => EachFile(p, ctx, f => MediaOps.Convert(f, arg, ctx)),
                    FileKind.Document when arg == "pdf" => EachFile(p, ctx, f => DocumentOps.ToPdf(f, ctx)),
                    FileKind.Document => EachFile(p, ctx, f => DocumentOps.OfficeConvert(f, arg, ctx)),
                    _ => JobResult.Fail(Loc.T("reason.unsupported")),
                };

            // ---- images
            case "img.compress": return EachFile(p, ctx, f => ImageOps.Compress(f, ctx));
            case "img.strip": return EachFile(p, ctx, f => ImageOps.StripMetadata(f, ctx));
            case "img.resize": return EachFile(p, ctx, f => ImageOps.Resize(f, arg, ctx));
            case "img.rotate": return EachFile(p, ctx, f => ImageOps.Rotate(f, int.Parse(arg), ctx));
            case "img.flip": return EachFile(p, ctx, f => ImageOps.Flip(f, arg == "h", ctx));
            case "img.crop" when arg == "auto": return EachFile(p, ctx, f => ImageOps.AutoTrim(f, ctx));
            case "img.crop" when arg.StartsWith("frac="):
            {
                var n = arg[5..].Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                return EachFile(p, ctx, f => ImageOps.CropFraction(f, new Rect(n[0], n[1], n[2], n[3]), ctx));
            }
            case "img.crop":
            {
                var parts = arg.Split('x');
                var ratio = double.Parse(parts[0]) / double.Parse(parts[1]);
                return EachFile(p, ctx, f => ImageOps.CropAspect(f, ratio, ctx));
            }
            case "img.mergepdf": return Single(ctx, () => PdfOps.ImagesToPdf(p.Paths, ctx), "result.merged");

            // ---- video
            case "vid.compress": return EachFile(p, ctx, f => MediaOps.Compress(f, arg, ctx));
            case "vid.extract": return EachFile(p, ctx, f => MediaOps.ExtractAudio(f, ctx));
            case "vid.mute": return EachFile(p, ctx, f => MediaOps.Mute(f, ctx));
            case "vid.snapshot": return EachFile(p, ctx, f => MediaOps.Snapshot(f, ctx));
            case "vid.rotate": return EachFile(p, ctx, f => MediaOps.Rotate(f, int.Parse(arg), ctx));
            case "vid.resize": return EachFile(p, ctx, f => MediaOps.Resize(f, int.Parse(arg), ctx));

            // ---- audio
            case "aud.compress": return EachFile(p, ctx, f => MediaOps.CompressAudio(f, ctx));
            case "aud.normalize": return EachFile(p, ctx, f => MediaOps.Normalize(f, ctx));
            case "aud.trim": return EachFile(p, ctx, f => MediaOps.TrimSilence(f, ctx));
            case "aud.mono": return EachFile(p, ctx, f => MediaOps.Mono(f, ctx));

            // ---- pdf
            case "pdf.merge": return Single(ctx, () => PdfOps.Merge(p.Paths, ctx), "result.merged");
            case "pdf.split": return EachFileMany(p, ctx, f => PdfOps.Split(f, ctx));
            case "pdf.rotate": return EachFile(p, ctx, f => PdfOps.Rotate(f, int.Parse(arg), ctx));
            case "pdf.compress": return EachFile(p, ctx, f => PdfOps.Compress(f, ctx));
            case "pdf.strip": return EachFile(p, ctx, f => PdfOps.StripMetadata(f, ctx));

            // ---- archives / any file
            case "arc.extract": return EachFile(p, ctx, f => FileOps.Extract(f, ctx));
            case "file.zip": return Single(ctx, () => FileOps.Zip(p.Paths, ctx), "result.zipped");
            case "file.hash": return Hash(p, ctx);

            // ---- actions
            case "act.copypath":
            {
                var text = string.Join(Environment.NewLine, p.Paths.Select(x => p.IsMultiple ? $"\"{x}\"" : x));
                return new JobResult { Outcome = JobOutcome.Info, MessageKey = "result.pathCopied", Clipboard = text };
            }
            case "act.rename":
            {
                var from = p.Paths[0].TrimEnd('\\');
                var to = Path.Combine(Path.GetDirectoryName(from)!, arg);
                if (string.Equals(from, to, StringComparison.Ordinal)) return new JobResult { Outcome = JobOutcome.Unchanged, MessageKey = "result.sameName" };
                if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && (File.Exists(to) || Directory.Exists(to)))
                    return JobResult.Fail(Loc.T("rename.exists"));
                FileOps.MovePath(from, to);
                return new JobResult
                {
                    Outcome = JobOutcome.Created,
                    Outputs = new[] { to },
                    MessageKey = "result.renamed",
                    Detail = arg,
                    Undo = new UndoRecord(Array.Empty<string>(), new[] { (from, to) }),
                };
            }
            case "act.move":
            {
                var folder = FileOps.KnownFolder(arg) ?? arg;
                var moves = FileOps.Move(p.Paths, folder, ctx);
                if (FileOps.KnownFolder(arg) is null) ctx.Settings.RememberFolder(folder);
                if (moves.Count == 0) return new JobResult { Outcome = JobOutcome.Unchanged, MessageKey = "result.alreadyThere" };
                return new JobResult
                {
                    Outcome = JobOutcome.Created,
                    Outputs = moves.Select(m => m.To).ToList(),
                    MessageKey = "result.moved",
                    Detail = folder,
                    Undo = new UndoRecord(Array.Empty<string>(), moves),
                };
            }
        }
        return JobResult.Fail(Loc.T("reason.unsupported") + " (" + id + ")");
    }

    static JobResult Single(JobContext ctx, Func<string> work, string key)
    {
        var output = work();
        ctx.ReportOverall(1);
        return JobResult.Created(new[] { output }, key);
    }

    static JobResult EachFile(DragPayload p, JobContext ctx, Func<string, string> work) =>
        EachFileMany(p, ctx, f => new[] { work(f) });

    /// <summary>Runs the work per file; one failure does not stop the batch.</summary>
    static JobResult EachFileMany(DragPayload p, JobContext ctx, Func<string, IReadOnlyList<string>> work)
    {
        var outputs = new List<string>();
        var failures = new List<string>();
        string? unchangedKey = null;
        for (var i = 0; i < p.Count; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            ctx.BeginItem(i, p.Count);
            try
            {
                outputs.AddRange(work(p.Paths[i]));
            }
            catch (UnchangedException u)
            {
                unchangedKey = u.MessageKey;
            }
            catch (UserFacingException e) when (p.IsMultiple)
            {
                failures.Add($"{Path.GetFileName(p.Paths[i])}: {e.Message}");
            }
        }
        ctx.ReportOverall(1);

        if (outputs.Count > 0)
        {
            string? detail = null;
            var skipped = p.Count - outputs.Count;
            if (failures.Count > 0) detail = Loc.F("result.partial", ("n", failures.Count)) + " — " + failures[0];
            else if (unchangedKey is not null && skipped > 0) detail = Loc.F("result.skipped", ("n", skipped));
            return JobResult.Created(outputs, "result.done", detail);
        }
        if (failures.Count > 0) return JobResult.Fail(failures[0]);
        return new JobResult { Outcome = JobOutcome.Unchanged, MessageKey = unchangedKey ?? "result.alreadyOptimal" };
    }

    static JobResult Hash(DragPayload p, JobContext ctx)
    {
        var lines = new List<string>();
        for (var i = 0; i < p.Count; i++)
        {
            ctx.BeginItem(i, p.Count);
            var h = FileOps.Sha256(p.Paths[i], ctx);
            lines.Add(p.IsMultiple ? $"{h}  {Path.GetFileName(p.Paths[i])}" : h);
        }
        var text = string.Join(Environment.NewLine, lines);
        return new JobResult
        {
            Outcome = JobOutcome.Info,
            MessageKey = "result.hashCopied",
            Detail = p.IsMultiple ? Loc.F("result.hashCount", ("n", p.Count)) : lines[0][..16] + "…",
            Clipboard = text,
        };
    }

    /// <summary>Reverts a job: created outputs go to the Recycle Bin, moves are moved back.</summary>
    public static bool Undo(UndoRecord record)
    {
        var ok = Native.Win32.Recycle(record.Created);
        foreach (var (from, to) in record.Moves.Reverse())
        {
            try
            {
                if ((File.Exists(to) || Directory.Exists(to)) && !File.Exists(from) && !Directory.Exists(from))
                    FileOps.MovePath(to, from);
            }
            catch (IOException)
            {
                ok = false;
            }
        }
        return ok;
    }
}
