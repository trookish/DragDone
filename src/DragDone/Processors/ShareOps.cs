using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using WinRT;
using Point = System.Windows.Point;

namespace DragDone.Processors;

/// <summary>
/// Opens the Windows share sheet (Nearby Sharing, Mail, Teams, …) for files, through the
/// desktop interop entry point of DataTransferManager.
/// </summary>
public static class ShareOps
{
    static Window? _anchor;

    public static bool Share(IReadOnlyList<string> paths, Point screenDip)
    {
        try
        {
            var anchor = Anchor(screenDip);
            var hwnd = new WindowInteropHelper(anchor).Handle;
            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            var iid = new Guid("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C"); // IDataTransferManager
            var ptr = interop.GetForWindow(hwnd, ref iid);
            var manager = MarshalInterface<DataTransferManager>.FromAbi(ptr);
            Marshal.Release(ptr);

            TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? handler = null;
            handler = async (sender, args) =>
            {
                sender.DataRequested -= handler;
                var deferral = args.Request.GetDeferral();
                try
                {
                    var items = new List<IStorageItem>();
                    foreach (var p in paths)
                    {
                        if (File.Exists(p)) items.Add(await StorageFile.GetFileFromPathAsync(p));
                        else if (Directory.Exists(p)) items.Add(await StorageFolder.GetFolderFromPathAsync(p));
                    }
                    args.Request.Data.Properties.Title = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files";
                    args.Request.Data.SetStorageItems(items);
                }
                finally
                {
                    deferral.Complete();
                }
            };
            manager.DataRequested += handler;
            interop.ShowShareUIForWindow(hwnd);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A transparent 1×1 window at the cursor: the share sheet positions itself near its owner.</summary>
    static Window Anchor(Point at)
    {
        _anchor ??= new Window
        {
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            Title = "DragDone share",
        };
        _anchor.Left = at.X;
        _anchor.Top = at.Y;
        _anchor.Show();
        _anchor.Activate();
        return _anchor;
    }

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr appWindow, ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }
}
