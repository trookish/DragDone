using System.Runtime.InteropServices;
using DragDone.Native;

namespace DragDone.Shell;

public enum MouseEvent
{
    Down,
    Move,
    Up,
}

/// <summary>
/// Global low-level mouse hook on its own thread. The callback only copies the event and
/// posts it on; Windows unhooks callbacks that take too long, so no work happens here.
/// </summary>
public sealed class MouseHook : IDisposable
{
    readonly Action<MouseEvent, Win32.POINT, bool> _sink;
    Thread? _thread;
    uint _threadId;
    IntPtr _hook;
    Win32.LowLevelMouseProc? _proc; // kept alive: the delegate must not be collected while hooked

    /// <param name="sink">Receives (event, point, isOwnNudge). Called on the hook thread; must be quick.</param>
    public MouseHook(Action<MouseEvent, Win32.POINT, bool> sink) => _sink = sink;

    public bool IsRunning => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_thread is not null) return;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _proc = Callback;
            _hook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _proc, Win32.GetModuleHandle(null), 0);
            ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (_hook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "DragDone mouse hook",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        ready.Wait(2000);
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (int)wParam;
            if (msg is Win32.WM_LBUTTONDOWN or Win32.WM_LBUTTONUP or Win32.WM_MOUSEMOVE)
            {
                var data = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                var own = data.dwExtraInfo == Win32.NudgeTag;
                var ev = msg switch
                {
                    Win32.WM_LBUTTONDOWN => MouseEvent.Down,
                    Win32.WM_LBUTTONUP => MouseEvent.Up,
                    _ => MouseEvent.Move,
                };
                _sink(ev, data.pt, own);
            }
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_thread is null) return;
        PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Win32.POINT pt;
    }

    [DllImport("user32.dll")]
    static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
}
