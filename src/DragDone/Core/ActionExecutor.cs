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
        id is "act.share" or "act.shelf" or "act.terminal" or "act.move:browse" or "img.crop:free" or "act.rename" or "act.print";

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
                    FileKind.Document => DocumentConvert(p, ctx, arg),
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

            // ---- documents: OOXML parts are read directly, so these need no Office installed
            case "doc.strip": return EachFile(p, ctx, f => OpenXmlOps.StripMetadata(f, ctx));
            case "doc.images": return EachFile(p, ctx, f => OpenXmlOps.ExtractImages(f, ctx));
            case "doc.text": return EachFile(p, ctx, f => OpenXmlOps.ExtractText(f, ctx));
            case "doc.stats": return Statistics(p, ctx);
            case "doc.compress": return EachFile(p, ctx, f => OpenXmlOps.CompressMedia(f, ctx));

            // ---- documents: these two go through the owning Office application
            case "doc.sheets": return EachFile(p, ctx, f => OfficeOps.SheetsToCsv(f, ctx));
            case "doc.slides": return EachFileMany(p, ctx, f => new[] { OfficeOps.SlidesToImages(f, "png", ctx) });

            // ---- actions
            case "act.print": return Print(p, ctx);
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

    /// <summary>
    /// Conversion for documents. Slide export writes a folder per file, so it is the many-outputs
    /// variant; every other target writes one file or fails with a localized reason.
    /// </summary>
    static JobResult DocumentConvert(DragPayload p, JobContext ctx, string token) => token switch
    {
        "pngslides" => EachFileMany(p, ctx, f => new[] { OfficeOps.SlidesToImages(f, "png", ctx) }),
        "jpgslides" => EachFileMany(p, ctx, f => new[] { OfficeOps.SlidesToImages(f, "jpg", ctx) }),
        _ => EachFile(p, ctx, f => DocumentOps.Convert(f, token, ctx)),
    };

    /// <summary>
    /// Words, characters and pages/slides/sheets, copied to the clipboard the way a hash is:
    /// the value is the point, so it is an Info result rather than a created file.
    /// </summary>
    static JobResult Statistics(DragPayload p, JobContext ctx)
    {
        var blocks = new List<string>();
        var summaries = new List<string>();
        for (var i = 0; i < p.Count; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            ctx.BeginItem(i, p.Count);
            var s = OpenXmlOps.ReadStats(p.Paths[i]);
            var lines = new List<string> { p.Paths[i] };
            lines.Add(Loc.F("stats.words", ("n", s.Words)));
            lines.Add(Loc.F("stats.characters", ("n", s.Characters)));
            var one = Loc.F("stats.wordsShort", ("n", s.Words));
            if (s.Sections > 0)
            {
                lines.Add(Loc.F(s.SectionsKey, ("n", s.Sections)));
                one += " · " + Loc.F(s.Sections == 1 ? s.SectionsOneKey : s.SectionsShortKey, ("n", s.Sections));
            }
            blocks.Add(string.Join(Environment.NewLine, lines));
            summaries.Add(one);
        }
        ctx.ReportOverall(1);
        return new JobResult
        {
            Outcome = JobOutcome.Info,
            MessageKey = "result.statsCopied",
            Detail = summaries[0],
            Clipboard = string.Join(Environment.NewLine, blocks),
        };
    }

    /// <summary>
    /// Hands every file to its default handler's print verb. Printing is the other app's job and
    /// may show a dialog, so this reports what was handed over, not what came out of the printer.
    /// </summary>
    static JobResult Print(DragPayload p, JobContext ctx)
    {
        var failures = new List<string>();
        var sent = 0;
        for (var i = 0; i < p.Count; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            ctx.BeginItem(i, p.Count);
            try
            {
                FileOps.Print(p.Paths[i]);
                sent++;
            }
            catch (UserFacingException e) when (p.IsMultiple)
            {
                failures.Add($"{Path.GetFileName(p.Paths[i])}: {e.Message}");
            }
        }
        ctx.ReportOverall(1);
        if (sent == 0) return JobResult.Fail(failures.Count > 0 ? failures[0] : Loc.T("error.print"));
        return new JobResult
        {
            Outcome = JobOutcome.Info,
            MessageKey = "result.printed",
            Detail = sent > 1 ? Loc.F("result.printedCount", ("n", sent)) : null,
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
