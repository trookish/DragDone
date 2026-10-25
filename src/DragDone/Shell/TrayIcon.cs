using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DragDone.Core;

namespace DragDone.Shell;

/// <summary>
/// The notification-area icon, driven straight through Shell_NotifyIcon.
///
/// Windows Forms used to own this, and with it the whole Windows Forms stack, which was the
/// largest single item in the published app. A tray icon needs only three things: a window to
/// receive the shell's callbacks, an HICON, and a menu — and WPF supplies the last one already.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>Which glyph a balloon carries. <see cref="App"/> shows the app's own icon.</summary>
    public enum BalloonIcon
    {
        App,
        Info,
        Warning,
    }

    const uint IconId = 1;
    const int CallbackMessage = 0x8001; // WM_APP + 1: ours alone

    const uint NIM_ADD = 0x0, NIM_MODIFY = 0x1, NIM_DELETE = 0x2, NIM_SETVERSION = 0x4;
    const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    const uint NotifyIconVersion4 = 4;

    // NIIF_ICON_MASK is an enum, not a set of bits: a balloon shows either a stock glyph or ours.
    const uint NIIF_INFO = 0x01, NIIF_WARNING = 0x02, NIIF_USER = 0x04, NIIF_LARGE_ICON = 0x20;

    const int WM_NULL = 0x0000;
    const int WM_CONTEXTMENU = 0x007B;
    const int WM_LBUTTONUP = 0x0202;
    const int WM_RBUTTONUP = 0x0205;
    const int NIN_SELECT = 0x0400;   // WM_USER: left click, version 4
    const int NIN_KEYSELECT = 0x0401; // keyboard activation, version 4

    const int SM_CXSMICON = 49;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_POPUP = unchecked((int)0x80000000);

    readonly HwndSource _window;
    readonly IntPtr _icon;
    readonly uint _taskbarCreated;
    readonly int _iconSize;
    string _text = "DragDone";
    ContextMenu? _menu;
    bool _added;
    bool _disposed;

    /// <summary>Left click on the icon.</summary>
    public event Action? LeftClick;

    /// <summary>Builds the menu to show on right click. Called on every right click, so the menu
    /// always reflects the settings as they are now.</summary>
    public Func<ContextMenu?>? MenuBuilder { get; set; }

    public TrayIcon()
    {
        _iconSize = IconSize();
        _icon = LoadAppIcon(_iconSize);
        _window = CreateMessageWindow();
        // Explorer broadcasts this when it restarts; the icon has to be put back by hand.
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _window.AddHook(WndProc);
        Add();
    }

    /// <summary>The tooltip text, which Windows also uses as the icon's accessible name.</summary>
    public string Text
    {
        get => _text;
        set
        {
            _text = Fit(value, 127);
            if (!_added) return;
            var data = Data(NIF_TIP | NIF_SHOWTIP);
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    // ------------------------------------------------------------------ shell calls

    void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            Diag.Log($"tray: icon rejected by the shell (icon={_icon != IntPtr.Zero}, {_iconSize}px)");
            return;
        }
        // Version 4 sends NIN_SELECT for a left click and WM_CONTEXTMENU for a right click, and
        // gives the shell the icon's own anchor point. Then ask for the standard tooltip.
        var version = Data(0);
        version.uVersion = NotifyIconVersion4;
        Shell_NotifyIcon(NIM_SETVERSION, ref version);
        var tip = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref tip);
        _added = true;
        Diag.Log($"tray: icon added at {_iconSize}px");
    }

    /// <summary>
    /// Shows a balloon. The shell turns these into toasts on Windows 10 and 11, where the app
    /// identity comes from the registering executable — so a warning keeps its warning glyph
    /// rather than being replaced by the app icon.
    /// </summary>
    public void ShowBalloon(string title, string text, BalloonIcon icon)
    {
        if (!_added) return;
        var data = Data(NIF_INFO);
        data.szInfoTitle = Fit(title, 63);
        data.szInfo = Fit(text, 255);
        data.dwInfoFlags = icon switch
        {
            BalloonIcon.Warning => NIIF_WARNING,
            BalloonIcon.Info => NIIF_INFO,
            _ => NIIF_USER | NIIF_LARGE_ICON,
        };
        data.hBalloonIcon = _icon;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    void ShowMenu()
    {
        var menu = MenuBuilder?.Invoke();
        if (menu is null) return;
        _menu = menu;
        menu.Closed += (_, _) =>
        {
            // The shell holds the foreground slot while a tray menu is up. Handing it back is
            // what lets the next click land on the window underneath instead of being eaten.
            PostMessage(_window.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            _menu = null;
        };
        // A menu owned by a process that does not own the foreground never closes on an outside
        // click: that click is spent activating the other window instead. Claim it first.
        SetForegroundWindow(_window.Handle);
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == _taskbarCreated)
        {
            // Explorer restarted and took every icon with it.
            Add();
            handled = true;
            return IntPtr.Zero;
        }
        if (message != CallbackMessage)
        {
            return IntPtr.Zero;
        }
        handled = true;
        // Version 4 carries the message in the low word and the icon id in the high word; earlier
        // versions send the message on its own.
        switch ((int)lParam.ToInt64() & 0xFFFF)
        {
            case NIN_SELECT:
            case NIN_KEYSELECT:
            case WM_LBUTTONUP:
                LeftClick?.Invoke();
                break;
            case WM_CONTEXTMENU:
            case WM_RBUTTONUP:
                ShowMenu();
                break;
        }
        return IntPtr.Zero;
    }

    NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _text,
        szInfo = "",
        szInfoTitle = "",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        _menu = null;
        if (_added)
        {
            var data = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        _window.RemoveHook(WndProc);
        _window.Dispose();
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    // ------------------------------------------------------------------ window and icon

    static HwndSource CreateMessageWindow()
    {
        // Not a message-only window (HWND_MESSAGE): those never receive the broadcast Explorer
        // sends when it restarts. This is an ordinary top-level window that is never shown, and
        // a tool window so it cannot turn up in Alt+Tab.
        var parameters = new HwndSourceParameters("DragDone.TrayIcon")
        {
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        };
        return new HwndSource(parameters);
    }

    /// <summary>The notification area's icon size at the DPI Windows is drawing the desktop at.</summary>
    static int IconSize()
    {
        var dpi = GetDpiForSystem();
        if (dpi > 0)
        {
            var scaled = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
            if (scaled > 0) return scaled;
        }
        return Math.Max(16, GetSystemMetrics(SM_CXSMICON));
    }

    static IntPtr LoadAppIcon(int size)
    {
        // An .ico holds one image per size. Take the smallest frame at least as large as the tray
        // wants and draw it down to exactly that size: letting Windows scale a 256px frame itself
        // is what leaves the icon soft at 150% and 200%.
        var decoder = BitmapDecoder.Create(
            new Uri("pack://application:,,,/Assets/DragDone.ico", UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames
            .OrderBy(f => f.PixelWidth >= size ? 0 : 1)
            .ThenBy(f => f.PixelWidth >= size ? f.PixelWidth : -f.PixelWidth)
            .First();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            dc.DrawImage(frame, new Rect(0, 0, size, size));
        }
        var rendered = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        return ToHIcon(rendered);
    }

    static IntPtr ToHIcon(BitmapSource image)
    {
        // Icons are straight (non-premultiplied) BGRA, while WPF renders premultiplied.
        BitmapSource bgra = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var stride = width * 4;
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down: the row order WPF hands out
            biPlanes = 1,
            biBitCount = 32,
        };
        var colour = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (colour == IntPtr.Zero) return IntPtr.Zero;
        bgra.CopyPixels(new Int32Rect(0, 0, width, height), bits, stride * height, stride);
        // A 32bpp icon takes its transparency from the alpha channel, so the AND mask stays empty.
        var mask = CreateBitmap(width, height, 1, 1, new byte[(width + 15) / 16 * 2 * height]);
        var info = new ICONINFO { fIcon = true, hbmColor = colour, hbmMask = mask };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(colour);
        DeleteObject(mask);
        return icon;
    }

    static string Fit(string? text, int max) => text is null ? "" : text.Length <= max ? text : text[..max];

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        /// <summary>A union: the balloon's lifetime for NIM_MODIFY, the shell version for NIM_SETVERSION.</summary>
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PostMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[]? bits);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeleteObject(IntPtr handle);
}
