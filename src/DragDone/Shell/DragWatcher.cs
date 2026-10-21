using System.Windows.Threading;
using DragDone.Core;
using DragDone.Native;
using DragDone.UI;

namespace DragDone.Shell;

/// <summary>
/// Glue between the global mouse hook, the drag state machine and the overlay.
/// Everything except the hook callback runs on the UI thread.
/// </summary>
public sealed class DragWatcher : IDisposable
{
    static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "CabinetWClass", // File Explorer
        "ExploreWClass", // legacy Explorer
        "Progman", // desktop
        "WorkerW", // desktop behind wallpaper slideshow
    };

    readonly Dispatcher _ui;
    readonly OverlayWindow _overlay;
    readonly Func<Settings> _settings;
    readonly DragStateMachine _machine;
    readonly MouseHook _hook;
    readonly DispatcherTimer _poll;
    readonly uint _ownPid = (uint)Environment.ProcessId;

    public DragWatcher(Dispatcher ui, OverlayWindow overlay, Func<Settings> settings)
    {
        _ui = ui;
        _overlay = overlay;
        _settings = settings;
        _machine = new DragStateMachine
        {
            ThresholdX = Math.Max(2, Win32.GetSystemMetrics(Win32.SM_CXDRAG)),
            ThresholdY = Math.Max(2, Win32.GetSystemMetrics(Win32.SM_CYDRAG)),
            Settings = settings(),
            PauseDelay = TimeSpan.FromMilliseconds(settings().DwellMs),
        };
        _hook = new MouseHook(OnHookEvent);
        _poll = new DispatcherTimer(DispatcherPriority.Input, ui) { Interval = TimeSpan.FromMilliseconds(30) };
        _poll.Tick += (_, _) => Tick();
        _overlay.Abandoned += () => _machine.Abandon();
    }

    public bool HookActive => _hook.IsRunning;

    /// <summary>A press or drag is in progress, or a job is still showing progress.</summary>
    public bool Busy => _machine.Phase != DragPhase.Idle || _overlay.IsShowing;

    public void Start()
    {
        _hook.Start();
        Diag.Log($"watcher start: hook running={_hook.IsRunning}, trigger={_settings().Trigger}, scope={_settings().Scope}, " +
                 $"keys={_settings().ConvertKey}/{_settings().ToolsKey}/{_settings().ActionsKey}, paused={_settings().Paused}");
    }

    /// <summary>Hook thread: copy and post. Never block here.</summary>
    void OnHookEvent(MouseEvent ev, Win32.POINT p, bool own)
    {
        if (own) return;
        _ui.BeginInvoke(DispatcherPriority.Input, () => Handle(ev, p));
    }

    void Handle(MouseEvent ev, Win32.POINT p)
    {
        var s = _settings();
        _machine.PauseDelay = TimeSpan.FromMilliseconds(s.DwellMs);
        var before = _machine.Phase;
        switch (ev)
        {
            case MouseEvent.Down:
                if (_overlay.IsShowing && !_overlay.IsBusy) _overlay.HideMenu();
                _machine.Trigger = s.Trigger;
                _machine.Settings = s;
                var eligible = !s.Paused && !_overlay.IsBusy && IsEligibleSource(p, s.Scope);
                _machine.OnDown(p, eligible);
                if (Diag.Enabled) Diag.Log($"down at {p.X},{p.Y}: eligible={eligible} class={RootClassAt(p)} -> {_machine.Phase}");
                if (_machine.Phase == DragPhase.Pressed) _poll.Start();
                break;
            case MouseEvent.Move:
                if (_machine.Phase is DragPhase.Pressed or DragPhase.Dragging)
                {
                    _machine.OnMove(p, DragStateMachine.ReadKeys(), DateTime.UtcNow);
                    Tick();
                }
                break;
            case MouseEvent.Up:
                Diag.Log($"up at {p.X},{p.Y} (phase {_machine.Phase})");
                _machine.OnUp();
                _overlay.OnMouseReleased();
                break;
        }
        if (Diag.Enabled && ev != MouseEvent.Down && _machine.Phase != before) Diag.Log($"phase {before} -> {_machine.Phase}");
    }

    static string RootClassAt(Win32.POINT p)
    {
        var hwnd = Win32.WindowFromPoint(p);
        return hwnd == IntPtr.Zero ? "(none)" : Win32.ClassName(Win32.GetAncestor(hwnd, Win32.GA_ROOT));
    }

    void Tick()
    {
        if (_machine.Phase == DragPhase.Dragging)
        {
            if (!Win32.IsDown(Win32.VK_LBUTTON))
            {
                Diag.Log("tick: left button no longer down while Dragging");
                _machine.OnUp();
            }
            else
            {
                var keys = DragStateMachine.ReadKeys();
                var tab = _machine.Poll(keys, DateTime.UtcNow);
                if (tab is not null)
                {
                    // Alt pressed and released alone would open the source window's menu bar afterwards.
                    if ((keys & KeyState.Alt) != 0) Win32.MaskAltMenu();
                    Win32.GetCursorPos(out var at);
                    Diag.Log($"summon {tab.Value} at {at.X},{at.Y} (keys {keys})");
                    _overlay.ShowAt(at, tab.Value);
                }
            }
        }

        if (_overlay.IsShowing && !_overlay.IsBusy && Win32.IsDown(Win32.VK_ESCAPE)) _overlay.HideMenu();

        if (_machine.Phase is DragPhase.Idle or DragPhase.Summoned && !_overlay.IsShowing) _poll.Stop();
        else if (_machine.Phase == DragPhase.Summoned && !Win32.IsDown(Win32.VK_LBUTTON))
        {
            _machine.OnUp();
            _overlay.OnMouseReleased();
        }
    }

    bool IsEligibleSource(Win32.POINT p, SourceScope scope)
    {
        var hwnd = Win32.WindowFromPoint(p);
        if (hwnd == IntPtr.Zero) return false;
        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        Win32.GetWindowThreadProcessId(root, out var pid);
        if (pid == _ownPid) return false;
        if (scope == SourceScope.Anywhere) return true;
        return ShellClasses.Contains(Win32.ClassName(root));
    }

    public void Dispose()
    {
        _poll.Stop();
        _hook.Dispose();
    }
}
