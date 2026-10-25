using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DragDone.Core;
using DragDone.Localization;
using DragDone.Native;
using DragDone.Processors;
using DragDone.UI;
using Microsoft.Win32;

namespace DragDone.Shell;

/// <summary>
/// The running app: tray icon, overlay, drag watcher, and the interactive actions
/// (share, shelf, terminal, crop, rename, browse) that need a window before the job runs.
/// </summary>
public sealed class AppHost : IOverlayHost, IDisposable
{
    readonly Dispatcher _ui;
    readonly OverlayWindow _overlay;
    readonly DragWatcher _watcher;
    readonly TrayIcon _tray;
    readonly bool _office;
    SettingsWindow? _settingsWindow;
    UndoRecord? _lastUndo;
    readonly DispatcherTimer _idleTrim;
    DispatcherTimer? _languageWatch;
    bool _trimmed;

    public Settings Settings { get; }

    public AppHost(Settings settings)
    {
        Settings = settings;
        _ui = Dispatcher.CurrentDispatcher;
        _office = DocumentOps.OfficeAvailable;
        var firstRun = !File.Exists(Settings.FilePath);
        ApplySettings(firstRun: true);
        // Write both files on the very first run: the docs promise a theme you can open and edit,
        // and an absent file is not editable.
        if (firstRun)
        {
            try { Settings.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex); }
        }

        _overlay = new OverlayWindow(this);
        _overlay.Prepare();
        _watcher = new DragWatcher(_ui, _overlay, () => Settings);

        _tray = new TrayIcon { MenuBuilder = BuildTrayMenu };
        _tray.LeftClick += ShowSettings;
        _tray.Text = TrayText;
        Loc.LanguageChanged += RefreshTray;

        _idleTrim = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.ApplicationIdle, (_, _) => TrimWhenIdle(), _ui);
        _idleTrim.Start();
        WatchSystemLanguage();
    }

    /// <summary>
    /// Follows Windows' display language while the app is running.
    ///
    /// Windows does raise a preference change when the language changes, but it reports
    /// the change under a category that does not identify the language, so the event
    /// alone cannot be trusted. A slow timer re-checks as well, which is what makes the
    /// app notice a language change without being restarted.
    /// </summary>
    void WatchSystemLanguage()
    {
        try
        {
            SystemEvents.UserPreferenceChanged += (_, _) => _ui.BeginInvoke(DispatcherPriority.Background, () => ApplySettings());
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            // Without the event the timer below still notices the change.
            Debug.WriteLine(ex);
        }

        _languageWatch = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) =>
        {
            if (Settings.LanguageMode != LanguageMode.System) return;
            var language = Loc.SystemLanguage();
            if (language is not null && language != Loc.Language) ApplySettings();
        }, _ui);
        _languageWatch.Start();
    }

    /// <summary>
    /// Between drags nothing of ours is on screen. Once that has been true for a full tick,
    /// collect and hand the working set back to Windows so the tray process stays light;
    /// pages fault back in within milliseconds when the next drag arrives.
    /// </summary>
    void TrimWhenIdle()
    {
        var busy = Application.Current.Windows.OfType<Window>().Any(w => w.IsVisible) || _watcher.Busy;
        if (busy)
        {
            _trimmed = false;
            return;
        }
        if (_trimmed) return;
        _trimmed = true;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Win32.SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
    }

    public void Start()
    {
        _watcher.Start();
        if (!_watcher.HookActive)
        {
            _tray.ShowBalloon("DragDone", Loc.T("tray.hookFailed"), TrayIcon.BalloonIcon.Warning);
        }
        if (Settings.ShowWelcome)
        {
            new WelcomeWindow(() =>
            {
                Settings.ShowWelcome = false;
                Settings.Save();
            }).Show();
        }
    }

    // ------------------------------------------------------------------ settings

    void ApplySettings(bool firstRun = false)
    {
        var language = Settings.ResolvedLanguage();
        var changed = language != Loc.Language;
        Loc.Set(language);
        Core.Diag.Log($"language: mode={Settings.LanguageMode} resolved={language} system={Loc.SystemLanguage() ?? "none"} changed={changed}");
        Theme.Attach(Settings);
        OutputPaths.OutputFolderOverride = Settings.OutputFolder;
        Ffmpeg.ResetCache();
        try
        {
            Startup.Apply(Settings.StartWithWindows);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            if (!firstRun) Debug.WriteLine(ex);
        }
        if (_tray is not null) RefreshTray();
    }

    public void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(Settings, () => ApplySettings());
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    string TrayText => Settings.Paused
        ? "DragDone — " + Loc.T("tray.pausedShort")
        : "DragDone — " + Loc.T("app.tagline");

    void RefreshTray()
    {
        if (_tray is not null) _tray.Text = TrayText;
    }

    /// <summary>
    /// Builds the tray menu for the settings and language as they are now. It is built on every
    /// right click rather than kept in step, so it can never show a state the app has moved past.
    /// </summary>
    ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();
        TrayMenu.Apply(menu, Loc.IsRtl);
        menu.Items.Add(new MenuItem { Header = "DragDone  ·  Drag • Press • Done", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.T("tray.settings"), ShowSettings));
        var pause = new MenuItem { Header = Loc.T("tray.pause"), IsCheckable = true, IsChecked = Settings.Paused };
        pause.Click += (_, _) =>
        {
            Settings.Paused = pause.IsChecked;
            Settings.Save();
            RefreshTray();
        };
        menu.Items.Add(pause);
        menu.Items.Add(Item(Loc.T("tray.shelf"), () =>
        {
            Win32.GetCursorPos(out var p);
            ShelfWindow.Instance.ShowNear(p);
        }));
        var undo = Item(Loc.T("tray.undoLast"), () =>
        {
            if (_lastUndo is null) return;
            var ok = ActionExecutor.Undo(_lastUndo);
            _lastUndo = null;
            _tray.ShowBalloon("DragDone", Loc.T(ok ? "result.undone" : "result.undoFailed"), TrayIcon.BalloonIcon.App);
        });
        undo.IsEnabled = _lastUndo is not null;
        menu.Items.Add(undo);
        var language = new MenuItem { Header = Loc.T("tray.language") };
        var modes = new (LanguageMode Mode, string Label)[]
        {
            (LanguageMode.System, Loc.T("lang.system")),
            (LanguageMode.Arabic, "العربية"),
            (LanguageMode.English, "English"),
        };
        foreach (var (mode, label) in modes)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = Settings.LanguageMode == mode };
            item.Click += (_, _) =>
            {
                Settings.LanguageMode = mode;
                Settings.Save();
                ApplySettings();
            };
            language.Items.Add(item);
        }
        menu.Items.Add(language);
        menu.Items.Add(Item(Loc.T("tray.help"), () => new WelcomeWindow(null).Show()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.T("tray.exit"), () => Application.Current.Shutdown()));
        return menu;
    }

    /// <summary>One tray row: a label and what choosing it does.</summary>
    static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // ------------------------------------------------------------------ IOverlayHost

    public Capabilities Capabilities() =>
        new(Ffmpeg.Locate(Settings.FfmpegPath) is not null, _office, Settings.RecentFolders, Settings.CustomActions);

    public void RememberUndo(UndoRecord record) => _lastUndo = record;

    public void RunInteractive(string actionId, DragPayload payload)
    {
        Win32.GetCursorPos(out var cursor);
        switch (actionId)
        {
            case "act.share":
                if (!ShareOps.Share(payload.Paths, ToDip(cursor)))
                    _tray.ShowBalloon("DragDone", Loc.T("error.share"), TrayIcon.BalloonIcon.Warning);
                break;

            case "act.shelf":
                ShelfWindow.Instance.Add(payload.Paths);
                ShelfWindow.Instance.ShowNear(cursor);
                break;

            case "act.terminal":
                OpenTerminal(payload.Paths[0]);
                break;

            case "act.move:browse":
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("action.moveTo") };
                if (dlg.ShowDialog() != true) return;
                Settings.RememberFolder(dlg.FolderName);
                Settings.Save();
                _ = _overlay.RunJobAsync("act.move:" + dlg.FolderName, payload, cursor);
                break;
            }

            case "act.rename":
            {
                var w = new RenameWindow(payload.Paths[0]);
                if (w.ShowDialog() != true || w.NewName is null) return;
                _ = _overlay.RunJobAsync("act.rename:" + w.NewName, payload, cursor);
                break;
            }

            case "img.crop:free":
            {
                System.Windows.Media.Imaging.BitmapSource image;
                try
                {
                    image = ImageOps.Load(payload.Paths[0], new JobContext(Settings));
                }
                catch (UserFacingException e)
                {
                    _tray.ShowBalloon("DragDone", e.Message, TrayIcon.BalloonIcon.Warning);
                    return;
                }
                var w = new CropWindow(image, Path.GetFileName(payload.Paths[0]), payload.Count);
                if (w.ShowDialog() != true || w.Result is not { } r) return;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var id = $"img.crop:frac={r.X.ToString(inv)},{r.Y.ToString(inv)},{r.Width.ToString(inv)},{r.Height.ToString(inv)}";
                _ = _overlay.RunJobAsync(id, payload, cursor);
                break;
            }
        }
    }

    static Point ToDip(Win32.POINT p)
    {
        var dpi = 96.0;
        var monitor = Win32.MonitorFromPoint(p, Win32.MONITOR_DEFAULTTONEAREST);
        if (Win32.GetDpiForMonitor(monitor, 0, out var dx, out _) == 0) dpi = dx;
        return new Point(p.X * 96.0 / dpi, p.Y * 96.0 / dpi);
    }

    static void OpenTerminal(string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        try
        {
            if (File.Exists(wt))
            {
                var psi = new ProcessStartInfo(wt) { UseShellExecute = false };
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(dir);
                Process.Start(psi);
            }
            else
            {
                Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = dir, UseShellExecute = true });
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = dir, UseShellExecute = true });
        }
    }

    public void Dispose()
    {
        _idleTrim.Stop();
        _watcher.Dispose();
        _tray.Dispose();
    }
}
