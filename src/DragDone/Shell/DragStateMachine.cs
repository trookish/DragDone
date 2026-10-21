using DragDone.Core;
using DragDone.Native;

namespace DragDone.Shell;

/// <summary>
/// Which keys are down right now. Shift/Ctrl/Alt double as Explorer's own drag modifiers, while
/// Win is free for DragDone to use. The mouse buttons are tracked too, but they are not offered
/// as triggers: Windows cancels a file drag the moment one is pressed.
/// </summary>
[Flags]
public enum KeyState
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
    Win = 8,
    Middle = 16,
    Right = 32,
}

public enum DragPhase
{
    Idle,

    /// <summary>Left button down on an eligible window; not yet past the drag threshold.</summary>
    Pressed,

    /// <summary>Moved past the threshold with the button held: possibly a file drag.</summary>
    Dragging,

    /// <summary>The menu was summoned for this drag.</summary>
    Summoned,
}

/// <summary>
/// Pure state machine that decides when to summon the menu. Inputs are mouse events,
/// modifier state and time; output is a <see cref="MenuTab"/> to open. No Win32 calls, so
/// every transition is unit-tested.
/// </summary>
public sealed class DragStateMachine
{
    public DragPhase Phase { get; private set; } = DragPhase.Idle;

    public int ThresholdX { get; init; } = 4;
    public int ThresholdY { get; init; } = 4;
    public TriggerMode Trigger { get; set; } = TriggerMode.Modifier;
    public TimeSpan PauseDelay { get; set; } = TimeSpan.FromMilliseconds(650);

    /// <summary>Which key opens which menu; set by the watcher from the saved settings.</summary>
    public Settings? Settings { get; set; }
    public int PauseTolerancePx { get; init; } = 4;

    Win32.POINT _down;
    Win32.POINT _restPoint;
    DateTime _restSince;
    KeyState _atDragStart;


    /// <param name="eligible">True when the press landed on a window files can be dragged from.</param>
    public void OnDown(Win32.POINT p, bool eligible)
    {
        Phase = eligible ? DragPhase.Pressed : DragPhase.Idle;
        _down = p;
    }

    public void OnMove(Win32.POINT p, KeyState keys, DateTime now)
    {
        switch (Phase)
        {
            case DragPhase.Pressed:
                if (Math.Abs(p.X - _down.X) > ThresholdX || Math.Abs(p.Y - _down.Y) > ThresholdY)
                {
                    Phase = DragPhase.Dragging;
                    // A modifier already held when the drag starts (Ctrl-drag to copy, Shift-drag to move)
                    // is the user's Explorer intent, not a summon; only a fresh press counts.
                    _atDragStart = keys;
                    _restPoint = p;
                    _restSince = now;
                }
                break;
            case DragPhase.Dragging:
                if (Math.Abs(p.X - _restPoint.X) > PauseTolerancePx || Math.Abs(p.Y - _restPoint.Y) > PauseTolerancePx)
                {
                    _restPoint = p;
                    _restSince = now;
                }
                break;
        }
    }

    public void OnUp() => Phase = DragPhase.Idle;

    /// <summary>Polled on a timer and on every move. Returns the tab to open, or null.</summary>
    public MenuTab? Poll(KeyState keys, DateTime now)
    {
        if (Phase != DragPhase.Dragging) return null;

        // Forget keys released since the drag began, so pressing them again counts as fresh.
        _atDragStart &= keys;
        var tab = TabFor(keys & ~_atDragStart);
        if (tab is null && Trigger == TriggerMode.ModifierOrPause && now - _restSince >= PauseDelay) tab = MenuTab.Convert;

        if (tab is not null) Phase = DragPhase.Summoned;
        return tab;
    }

    /// <summary>The tab a just-pressed key opens, honouring the user's bindings.</summary>
    MenuTab? TabFor(KeyState fresh)
    {
        if (fresh == KeyState.None) return null;
        var s = Settings ?? new Settings();
        var bindings = new[]
        {
            (s.ConvertKey, MenuTab.Convert),
            (s.ToolsKey, MenuTab.Tools),
            (s.ActionsKey, MenuTab.Actions),
        };
        foreach (var (key, tab) in bindings)
        {
            if (key.Length == 0) continue;
            var bit = ToState(key);
            if (bit != KeyState.None && (fresh & bit) != 0) return tab;
        }
        return null;
    }

    /// <summary>The overlay decided this was not a file drag (no OLE data arrived).</summary>
    public void Abandon()
    {
        if (Phase == DragPhase.Summoned) Phase = DragPhase.Idle;
    }

    public static KeyState ToState(string key) => key switch
    {
        "Shift" => KeyState.Shift,
        "Ctrl" => KeyState.Ctrl,
        "Alt" => KeyState.Alt,
        "Win" => KeyState.Win,
        _ => KeyState.None,
    };

    public static KeyState ReadKeys() =>
        (Win32.IsDown(Win32.VK_SHIFT) ? KeyState.Shift : 0) |
        (Win32.IsDown(Win32.VK_CONTROL) ? KeyState.Ctrl : 0) |
        (Win32.IsDown(Win32.VK_MENU) ? KeyState.Alt : 0) |
        (Win32.IsDown(Win32.VK_LWIN) || Win32.IsDown(Win32.VK_RWIN) ? KeyState.Win : 0) |
        (Win32.IsDown(Win32.VK_MBUTTON) ? KeyState.Middle : 0) |
        (Win32.IsDown(Win32.VK_RBUTTON) ? KeyState.Right : 0);
}
