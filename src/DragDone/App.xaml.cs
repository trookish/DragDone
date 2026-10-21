using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DragDone.Core;
using DragDone.Localization;
using DragDone.Shell;
using DragDone.UI;

[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]

namespace DragDone;

/// <summary>
/// Entry point. Modes:
/// <list type="bullet">
/// <item>(default) / <c>--tray</c>: single tray instance.</item>
/// <item><c>--settings</c>: open settings (in the running instance if there is one).</item>
/// <item><c>--run &lt;actionId&gt; &lt;files…&gt; [--report file.json] [--lang ar|en]</c>: run one action headless.</item>
/// <item><c>--snapshot &lt;folder&gt;</c>: render the menu in many states to PNG (visual checks, docs).</item>
/// <item><c>--lang-info</c>: report which language the app would use, and why.</item>
/// <item><c>--show &lt;window&gt; [ar|en]</c>: open one window standalone (for screenshots).</item>
/// </list>
/// </summary>
public partial class App : Application
{
    const string MutexName = @"Local\DragDone.SingleInstance";
    const string SignalName = @"Local\DragDone.ShowSettings";

    Mutex? _mutex;
    EventWaitHandle? _signal;
    AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        // A shortcut or an installer can start us with a working directory that no longer exists.
        // Children inherit it, so every process launch would fail with "the directory name is
        // invalid" — which is exactly how video and audio jobs used to die. Move somewhere real.
        try
        {
            if (!Directory.Exists(Environment.CurrentDirectory))
                Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Diag.Log($"working directory left alone: {ex.Message}");
        }
        var args = e.Args;

        if (args.Length > 0 && args[0] == "--run")
        {
            Shutdown(RunHeadless(args));
            return;
        }
        if (args.Length > 1 && args[0] == "--snapshot")
        {
            Shutdown(Snapshots.Render(args[1], args.Skip(2).ToArray()));
            return;
        }
        if (args.Length > 0 && args[0] == "--lang-info")
        {
            // Reports which language the app would use and why. Useful when a machine is
            // set to a language DragDone does not ship, or after a Windows language change.
            var current = Settings.Load();
            var resolved = current.ResolvedLanguage();
            // Apply it, so the reported flow direction is the one the app will really use.
            Loc.Set(resolved);
            var text = string.Join(Environment.NewLine, new[]
            {
                $"LanguageMode   = {current.LanguageMode}",
                $"SystemLanguage = {Loc.SystemLanguage() ?? "(none we ship)"}",
                $"Resolved       = {resolved}",
                $"Flow           = {Loc.Flow}",
            });
            var target = Array.IndexOf(args, "--out") is var oi and > 0 && oi + 1 < args.Length ? args[oi + 1] : null;
            if (target is not null) File.WriteAllText(target, text);
            if (Native.Win32.AttachConsole(-1)) Console.WriteLine(text);
            Shutdown(0);
            return;
        }
        if (args.Length > 0 && args[0] == "--identity")
        {
            // Says whether this copy is packaged, what identity Windows gave it, and where its
            // settings really live — packaged apps get a virtualised %APPDATA%, which is not the
            // path the installer channel documents.
            var packaged = Shell.PackageInfo.IsPackaged;
            var lines = new List<string>
            {
                $"packaged       = {packaged}",
                $"name           = {Shell.PackageInfo.Name ?? "(unpackaged)"}",
                $"family         = {Shell.PackageInfo.FamilyName ?? "(unpackaged)"}",
                $"version        = {Shell.PackageInfo.Version ?? "(unpackaged)"}",
                $"install        = {Shell.PackageInfo.InstallLocation ?? AppContext.BaseDirectory}",
                $"startup        = {Shell.Startup.Describe()}",
                $"settings       = {Settings.Folder}",
                $"executable     = {Environment.ProcessPath}",
            };
            var identityText = string.Join(Environment.NewLine, lines);
            var identityTarget = Array.IndexOf(args, "--out") is var ii and > 0 && ii + 1 < args.Length ? args[ii + 1] : null;
            if (identityTarget is not null) File.WriteAllText(identityTarget, identityText);
            if (Native.Win32.AttachConsole(-1)) Console.WriteLine(identityText);
            Shutdown(0);
            return;
        }
        if (args.Length > 1 && args[0] == "--show")
        {
            var capture = Array.IndexOf(args, "--capture") is var ci and > 0 && ci + 1 < args.Length ? args[ci + 1] : null;
            // --live renders the user's own settings and theme file instead of the demo look.
            // --filter narrows the settings window to the matching rows, for screenshots.
            var filter = Array.IndexOf(args, "--filter") is var fi and > 0 && fi + 1 < args.Length ? args[fi + 1] : null;
            // --scroll captures the page scrolled down, which is where the preview floats.
            double? scroll = Array.IndexOf(args, "--scroll") is var si and > 0 && si + 1 < args.Length
                && double.TryParse(args[si + 1], out var offset) ? offset : null;
            Snapshots.ShowWindow(args[1], args.Length > 2 && !args[2].StartsWith("--") ? args[2] : null, capture, args.Contains("--live"), filter, scroll);
            return;
        }
        if (args.Length > 0 && args[0] == "--selftest")
        {
            // A WinExe has no console of its own; --out is the dependable way to read the report.
            var outPath = Array.IndexOf(args, "--out") is var oi and > 0 && oi + 1 < args.Length ? args[oi + 1] : null;
            Native.Win32.AttachConsole(-1);
            int code;
            if (args.Length > 1 && args[1] == "settings")
            {
                code = Snapshots.SelfTest(outPath);
            }
            else if (args.Length > 1 && args[1] == "shelf")
            {
                code = Snapshots.SelfTestShelf(outPath);
            }
            else
            {
                Console.WriteLine("usage: --selftest settings|shelf [--out report.txt]");
                code = 2;
            }
            Console.Out.Flush();
            Shutdown(code);
            return;
        }

        _mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // Already running: ask that instance to open its settings, then leave.
            try
            {
                EventWaitHandle.OpenExisting(SignalName).Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(0);
            return;
        }

        var settings = Settings.Load();
        _host = new AppHost(settings);
        _host.Start();
        if (args.Contains("--settings")) _host.ShowSettings();

        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        var listener = new Thread(() =>
        {
            while (_signal.WaitOne())
            {
                Dispatcher.BeginInvoke(() => _host?.ShowSettings());
            }
        })
        {
            IsBackground = true,
            Name = "DragDone instance signal",
        };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    static int RunHeadless(string[] args)
    {
        var id = args.Length > 1 ? args[1] : "";
        string? report = null;
        var files = new List<string>();
        var settings = Settings.Load();
        var lang = settings.ResolvedLanguage();
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--report" && i + 1 < args.Length) report = args[++i];
            else if (args[i] == "--lang" && i + 1 < args.Length) lang = args[++i];
            else files.Add(Path.GetFullPath(args[i]));
        }
        Loc.Set(lang);
        OutputPaths.OutputFolderOverride = settings.OutputFolder;

        var payload = new DragPayload(files);
        var result = ActionExecutor.Execute(id, payload, new JobContext(settings));
        var json = JsonSerializer.Serialize(new
        {
            action = id,
            kind = payload.Kind.ToString(),
            outcome = result.Outcome.ToString(),
            message = Loc.T(result.MessageKey),
            detail = result.Detail,
            outputs = result.Outputs,
            clipboard = result.Clipboard,
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (report is not null) File.WriteAllText(report, json);
        else if (Native.Win32.AttachConsole(-1)) Console.WriteLine(json);
        return result.Outcome is JobOutcome.Failed or JobOutcome.Cancelled ? 1 : 0;
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.AppendAllText(Path.Combine(Settings.Folder, "error.log"), $"[{DateTime.Now:s}] {e.Exception}\n\n");
        }
        catch (IOException) { }
        // A tray utility must not vanish because one action misbehaved.
        e.Handled = _host is not null;
    }
}
