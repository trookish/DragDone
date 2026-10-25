using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using DragDone.Core;
using DragDone.Localization;

namespace DragDone.Processors;

/// <summary>
/// Office documents, driven through the application that owns them (late-bound COM, so there are no
/// interop assemblies to ship). Two rules matter more than the conversions themselves:
///
/// <para><b>Never quit the user's session.</b> PowerPoint is single-instance: <c>CreateInstance</c>
/// attaches to an instance that is already open, and calling <c>Quit</c> on it closes the user's
/// presentations. Word and Excel have the same hazard in practice (they reuse a registered running
/// instance). So the process is only asked to quit when we started it and nothing is open in it.</para>
///
/// <para><b>Never sit on a hidden password prompt.</b> An encrypted OOXML file is not a zip but an
/// OLE compound file, which is cheap to detect before any application is started; the formats whose
/// <c>Open</c> takes a password (Word and Excel) also get a dummy one, so a protected file fails at
/// once instead of waiting for input on a window the user cannot see.</para>
///
/// Must run on an STA thread.
/// </summary>
public static class OfficeOps
{
    /// <summary>A password no document has: passing it turns a prompt into an immediate failure.</summary>
    const string NoPassword = "\u0001dragdone";

    // Word: wdExportFormatPDF / wdExportFormatXPS; wdFormat* save formats.
    const int WdFormatDocument = 0;
    const int WdFormatRtf = 6;
    const int WdFormatUnicodeText = 7;
    const int WdFormatFilteredHtml = 10;
    const int WdFormatDocumentDefault = 16;
    const int WdFormatOpenDocumentText = 23;
    const int WdExportPdf = 17;
    const int WdExportXps = 18;

    // Excel: xlTypePDF / xlTypeXPS for ExportAsFixedFormat; xl* file formats for SaveAs.
    const int XlTypePdf = 0;
    const int XlTypeXps = 1;
    const int XlOpenXmlWorkbook = 51;
    const int XlWorkbookNormal = 56;
    const int XlOpenDocumentSpreadsheet = 60;
    const int XlCsvUtf8 = 62;   // Excel 2016+
    const int XlCsv = 6;
    const int XlHtml = 44;

    // PowerPoint: ppSaveAs* formats.
    const int PpSaveAsPresentation = 1;
    const int PpSaveAsOpenXmlPresentation = 24;
    const int PpSaveAsPdf = 32;
    const int PpSaveAsXps = 33;
    const int PpSaveAsOpenDocumentPresentation = 35;

    // Visio / Publisher: fixed-format PDF.
    const int FixedFormatPdf = 1;

    public static bool AnyInstalled => OfficeApps.Any;

    // ------------------------------------------------------------------ convert

    /// <summary>
    /// Converts one Office document to <paramref name="token"/> (the output extension) and returns
    /// the path written. Availability is checked first so a missing app produces a clear reason
    /// instead of a COM failure.
    /// </summary>
    public static string Convert(string source, string token, JobContext ctx)
    {
        var family = FileKinds.Office(source);
        if (family == OfficeFamily.None) throw new UserFacingException(Loc.T("reason.unsupported"));
        if (!OfficeApps.Installed(family)) throw new UserFacingException(Loc.T(Capabilities.MissingKey(family)));
        GuardEncrypted(source);
        ctx.Report(0.05);

        return family switch
        {
            OfficeFamily.Word => Word(source, token),
            OfficeFamily.Excel => Excel(source, token),
            OfficeFamily.PowerPoint => PowerPoint(source, token, ctx),
            OfficeFamily.Visio => Visio(source, token),
            _ => Publisher(source, token),
        };
    }

    static string Word(string source, string token)
    {
        var output = OutputPaths.For(source, token);
        Protect(output, () => Open(OfficeFamily.Word, app =>
        {
            app.DisplayAlerts = 0;
            dynamic doc = app.Documents.Open(Full(source), false, true, false, NoPassword);
            try
            {
                switch (token)
                {
                    case "pdf": doc.ExportAsFixedFormat(output, WdExportPdf); break;
                    case "xps": doc.ExportAsFixedFormat(output, WdExportXps); break;
                    case "docx": doc.SaveAs2(output, WdFormatDocumentDefault); break;
                    case "doc": doc.SaveAs2(output, WdFormatDocument); break;
                    case "rtf": doc.SaveAs2(output, WdFormatRtf); break;
                    case "odt": doc.SaveAs2(output, WdFormatOpenDocumentText); break;
                    // Unicode text, not the ANSI writer: Arabic survives the trip either way.
                    case "txt": doc.SaveAs2(output, WdFormatUnicodeText); break;
                    case "html": doc.SaveAs2(output, WdFormatFilteredHtml); break;
                    default: throw new UserFacingException(Loc.T("reason.unsupported"));
                }
            }
            finally
            {
                doc.Close(false);
            }
        }));
        return Ensure(output);
    }

    static string Excel(string source, string token)
    {
        var output = OutputPaths.For(source, token);
        // Excel lays pages out through a print driver, so its PDF/XPS export is the one conversion
        // that cannot work on a machine with no printer at all.
        var prints = token is "pdf" or "xps";
        Protect(output, () => Open(OfficeFamily.Excel, app =>
        {
            app.DisplayAlerts = false;
            dynamic wb = app.Workbooks.Open(Full(source), 0, true, Type.Missing, NoPassword, NoPassword);
            try
            {
                switch (token)
                {
                    case "pdf": wb.ExportAsFixedFormat(XlTypePdf, output); break;
                    case "xps": wb.ExportAsFixedFormat(XlTypeXps, output); break;
                    case "xlsx": wb.SaveAs(output, XlOpenXmlWorkbook); break;
                    case "xls": wb.SaveAs(output, XlWorkbookNormal); break;
                    case "csv": Csv(wb, output); break;
                    case "ods": wb.SaveAs(output, XlOpenDocumentSpreadsheet); break;
                    case "html": wb.SaveAs(output, XlHtml); break;
                    default: throw new UserFacingException(Loc.T("reason.unsupported"));
                }
            }
            finally
            {
                wb.Close(false);
            }
        }, prints));
        return Ensure(output);
    }

    static string PowerPoint(string source, string token, JobContext ctx)
    {
        if (token is "pngslides" or "jpgslides")
            return SlidesToImages(source, token == "pngslides" ? "png" : "jpg", ctx);

        var output = OutputPaths.For(source, token);
        Protect(output, () => Open(OfficeFamily.PowerPoint, app =>
        {
            dynamic pres = OpenPresentation(app, source);
            try
            {
                switch (token)
                {
                    case "pdf": pres.SaveAs(output, PpSaveAsPdf); break;
                    case "xps": pres.SaveAs(output, PpSaveAsXps); break;
                    case "pptx": pres.SaveAs(output, PpSaveAsOpenXmlPresentation); break;
                    case "ppt": pres.SaveAs(output, PpSaveAsPresentation); break;
                    case "odp": pres.SaveAs(output, PpSaveAsOpenDocumentPresentation); break;
                    default: throw new UserFacingException(Loc.T("reason.unsupported"));
                }
            }
            finally
            {
                pres.Close();
            }
        }));
        return Ensure(output);
    }

    static string Visio(string source, string token)
    {
        if (token != "pdf") throw new UserFacingException(Loc.T("reason.unsupported"));
        var output = OutputPaths.For(source, "pdf");
        Protect(output, () => Open(OfficeFamily.Visio, app =>
        {
            dynamic doc = app.Documents.Open(Full(source));
            try { doc.ExportAsFixedFormat(output, FixedFormatPdf); }
            finally { doc.Close(); }
        }));
        return Ensure(output);
    }

    static string Publisher(string source, string token)
    {
        if (token != "pdf") throw new UserFacingException(Loc.T("reason.unsupported"));
        var output = OutputPaths.For(source, "pdf");
        Protect(output, () => Open(OfficeFamily.Publisher, app =>
        {
            dynamic doc = app.Open(Full(source));
            try { doc.ExportAsFixedFormat(output, FixedFormatPdf); }
            finally { doc.Close(); }
        }));
        return Ensure(output);
    }

    // ------------------------------------------------------------------ COM extras

    /// <summary>
    /// Exports every slide of a presentation as its own image, into a folder beside the deck. The
    /// images keep the deck's own aspect ratio at 1920 px wide, so a 16:9 deck does not come out
    /// stretched by a fixed 4:3 guess.
    /// </summary>
    public static string SlidesToImages(string source, string format, JobContext ctx)
    {
        if (!OfficeApps.Installed(OfficeFamily.PowerPoint))
            throw new UserFacingException(Loc.T(Capabilities.MissingKey(OfficeFamily.PowerPoint)));
        GuardEncrypted(source);

        var folder = OutputPaths.FolderNear(source, "suffix.slides");
        Directory.CreateDirectory(folder);
        try
        {
            Open(OfficeFamily.PowerPoint, app =>
            {
                dynamic pres = OpenPresentation(app, source);
                try
                {
                    double width = pres.PageSetup.SlideWidth;
                    double height = pres.PageSetup.SlideHeight;
                    var px = 1920;
                    var py = (int)Math.Round(px * height / Math.Max(1d, width));
                    pres.Export(folder, format == "jpg" ? "JPG" : "PNG", px, py);
                }
                finally
                {
                    pres.Close();
                }
            });
            ctx.Token.ThrowIfCancellationRequested();
            if (Directory.GetFiles(folder).Length == 0) throw new UserFacingException(Loc.T("error.office"));
        }
        catch
        {
            FileOps.TryDeleteFolder(folder);
            throw;
        }
        return folder;
    }

    /// <summary>
    /// Writes one CSV per worksheet into a folder beside the workbook. The workbook is opened
    /// read-only, and saving a single sheet to CSV does not write to it: the source stays untouched.
    /// </summary>
    public static string SheetsToCsv(string source, JobContext ctx)
    {
        if (!OfficeApps.Installed(OfficeFamily.Excel))
            throw new UserFacingException(Loc.T(Capabilities.MissingKey(OfficeFamily.Excel)));
        GuardEncrypted(source);

        var folder = OutputPaths.FolderNear(source, "suffix.sheets");
        Directory.CreateDirectory(folder);
        var written = 0;
        try
        {
            Open(OfficeFamily.Excel, app =>
            {
                app.DisplayAlerts = false;
                dynamic wb = app.Workbooks.Open(Full(source), 0, true, Type.Missing, NoPassword, NoPassword);
                try
                {
                    var sheets = (int)wb.Worksheets.Count;
                    for (var i = 1; i <= sheets; i++)
                    {
                        ctx.Token.ThrowIfCancellationRequested();
                        dynamic sheet = wb.Worksheets.Item(i);
                        var name = SafeName((string)sheet.Name);
                        Csv(sheet, OutputPaths.Unique(Path.Combine(folder, name + ".csv")));
                        written++;
                        ctx.Report((double)i / Math.Max(1, sheets));
                    }
                }
                finally
                {
                    wb.Close(false);
                }
            });
        }
        catch
        {
            FileOps.TryDeleteFolder(folder);
            throw;
        }
        if (written == 0) throw new UnchangedException("result.emptyDocument");
        return folder;
    }

    /// <summary>xlCSVUTF8 keeps Arabic and accents; very old Excel only knows the ANSI writer.</summary>
    static void Csv(dynamic target, string output)
    {
        try
        {
            target.SaveAs(output, XlCsvUtf8);
        }
        catch (COMException)
        {
            target.SaveAs(output, XlCsv);
        }
    }

    // ------------------------------------------------------------------ application lifecycle

    /// <summary>
    /// Runs <paramref name="work"/> against the family's application, creating it only if needed,
    /// and quits it only when this call created it and left no document behind. Anything the user
    /// already had open keeps running.
    /// </summary>
    static void Open(OfficeFamily family, Action<dynamic> work, bool prints = false)
    {
        var missing = Loc.T(Capabilities.MissingKey(family));
        var progId = OfficeApps.ProgId(family) ?? throw new UserFacingException(missing);
        var type = Type.GetTypeFromProgID(progId) ?? throw new UserFacingException(missing);
        var wasRunning = OfficeApps.IsRunning(family);
        var before = Pids(family);

        dynamic? app = null;
        var mine = new List<int>();
        try
        {
            app = Activator.CreateInstance(type);
            // Whatever appeared in the process list is ours; only that may ever be closed or ended.
            mine = Pids(family).Except(before).ToList();
            // Never make the user's window disappear or flash: only touch it when we own it.
            if (!wasRunning) Try(() => { if (family is OfficeFamily.Word or OfficeFamily.Excel) app!.Visible = false; });
            Try(() =>
            {
                switch (family)
                {
                    case OfficeFamily.Word: app!.DisplayAlerts = 0; break;
                    case OfficeFamily.Excel: app!.DisplayAlerts = false; break;
                    case OfficeFamily.PowerPoint: app!.DisplayAlerts = 1; break; // ppAlertsNone
                    default: app!.DisplayAlerts = false; break;
                }
            });
            // msoAutomationSecurityForceDisable: a document with macros must never run them.
            Try(() => app!.AutomationSecurity = 3);
            work(app);
        }
        catch (COMException ex) when (prints && IsPrintingError(ex))
        {
            throw new UserFacingException(Loc.T("error.noPrinter"));
        }
        catch (COMException ex) when (IsPasswordError(ex))
        {
            throw new UserFacingException(Loc.T("error.passwordProtected"));
        }
        catch (COMException)
        {
            throw new UserFacingException(Loc.T("error.office"));
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            throw new UserFacingException(Loc.T("error.office"));
        }
        finally
        {
            Release(family, app, wasRunning, mine);
        }
    }

    static void Release(OfficeFamily family, dynamic? app, bool wasRunning, IReadOnlyList<int> mine)
    {
        try
        {
            // The whole point: a process that was already running belongs to the user, and it is
            // never quit, no matter how many of their documents are open. Only an application this
            // call started can be closed and quit — and then nothing in it can be the user's.
            if (app is not null && !wasRunning)
            {
                // We started this process, so every document in it is ours: a failing conversion
                // can leave one half-open, and quitting then would leave the process behind.
                CloseDocuments(family, app);
                app.Quit();
            }
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidOperationException)
        {
            // Office already gone, or too broken to answer; the reaper below settles it.
        }
        finally
        {
            if (app is not null)
            {
                try { Marshal.FinalReleaseComObject(app); }
                catch (Exception) { /* the RCW is unusable after a crash; the GC will finish it */ }
            }
            // An alert Office shows without a window can block Quit forever (Excel's "no printers"
            // is one), and Excel can start a second process beside the user's window. Either way the
            // process appeared during this call, so it is ours to end.
            Reap(mine);
        }
    }

    /// <summary>Ends the processes this call started, if Quit could not.</summary>
    static void Reap(IReadOnlyList<int> mine)
    {
        foreach (var pid in mine)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                // Quit usually takes a moment; only a process that ignores it is killed.
                if (process.WaitForExit(3000)) continue;
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or no longer ours to end.
            }
        }
    }

    /// <summary>The processes of one Office family, by the name the vendor ships them under.</summary>
    static List<int> Pids(OfficeFamily family)
    {
        var name = family switch
        {
            OfficeFamily.Word => "WINWORD",
            OfficeFamily.Excel => "EXCEL",
            OfficeFamily.PowerPoint => "POWERPNT",
            OfficeFamily.Visio => "VISIO",
            OfficeFamily.Publisher => "MSPUB",
            _ => "",
        };
        if (name.Length == 0) return new List<int>();
        try
        {
            return Process.GetProcessesByName(name).Select(p => p.Id).ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new List<int>();
        }
    }

    /// <summary>Closes the documents left open in a process we started, never saving anything.</summary>
    static void CloseDocuments(OfficeFamily family, dynamic app)
    {
        try
        {
            switch (family)
            {
                case OfficeFamily.Word:
                    while ((int)app.Documents.Count > 0) app.Documents[1].Close(false);
                    break;
                case OfficeFamily.Excel:
                    while ((int)app.Workbooks.Count > 0) app.Workbooks[1].Close(false);
                    break;
                case OfficeFamily.PowerPoint:
                    while ((int)app.Presentations.Count > 0) app.Presentations[1].Close();
                    break;
                case OfficeFamily.Visio:
                case OfficeFamily.Publisher:
                    while ((int)app.Documents.Count > 0) app.Documents[1].Close();
                    break;
            }
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidOperationException)
        {
            // Quit is still worth a try: Office may close the rest itself.
        }
    }

    static dynamic OpenPresentation(dynamic app, string source) =>
        // ReadOnly, no untitled copy, no window: a hidden window never steals focus.
        app.Presentations.Open(Full(source), -1, 0, 0);

    // ------------------------------------------------------------------ guards

    /// <summary>
    /// An encrypted OOXML file is an OLE compound file (D0 CF 11 E0), not the zip its extension
    /// promises. Detecting that before <c>Open</c> is what stops Office asking for a password on a
    /// window nobody can see.
    /// </summary>
    public static bool IsEncrypted(string path)
    {
        if (!FileKinds.IsOpenXml(path)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[8];
            var read = stream.Read(head);
            return read >= 4 && head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    static void GuardEncrypted(string path)
    {
        if (IsEncrypted(path)) throw new UserFacingException(Loc.T("error.passwordProtected"));
    }

    /// <summary>
    /// Excel's PDF export reports "no printers are installed" as a generic application error, so the
    /// message is only useful if the machine really cannot print: no spooler or no queue.
    /// </summary>
    static bool IsPrintingError(COMException ex)
    {
        if (ex.HResult != unchecked((int)0x800A03EC)) return false;
        try
        {
            using var server = new System.Printing.LocalPrintServer();
            return !server.GetPrintQueues().Any();
        }
        catch (Exception)
        {
            return true;   // the spooler is not reachable either, which is the same problem
        }
    }

    static bool IsPasswordError(COMException ex)
    {
        if (ex.HResult == unchecked((int)0x800A5408)) return true;   // Word: wrong password
        if (ex.HResult == unchecked((int)0x800A1521)) return true;   // Excel: file is password protected
        var message = ex.Message;
        return message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("كلمة المرور", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    static string Full(string path) => Path.GetFullPath(path);

    /// <summary>Office reports success without writing more often than it should; check the file.</summary>
    static string Ensure(string output)
    {
        if (!File.Exists(output)) throw new UserFacingException(Loc.T("error.office"));
        return output;
    }

    /// <summary>Deletes a half-written output so a failed job leaves nothing puzzling behind.</summary>
    static void Protect(string output, Action work)
    {
        try
        {
            work();
        }
        catch
        {
            Ffmpeg.TryDelete(output);
            throw;
        }
    }

    static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "sheet" : clean;
    }

    static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidOperationException)
        {
            // Not every application supports every property; the ones that do not simply skip it.
        }
    }
}
