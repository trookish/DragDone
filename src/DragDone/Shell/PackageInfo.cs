using System.Runtime.InteropServices;

namespace DragDone.Shell;

/// <summary>
/// Whether this copy runs from an MSIX package and, if so, the identity Windows handed it.
///
/// Two channels ship the same binary: the Inno Setup installer writes a Run key for startup, and
/// the Store package asks Windows for a startup task instead. Registry writes from a packaged app
/// are virtualised into the package, so a Run key written there is never read — the branch has to
/// be chosen at runtime, which is what this type answers.
/// </summary>
public static class PackageInfo
{
    public static bool IsPackaged
    {
        get
        {
            try
            {
                _ = Windows.ApplicationModel.Package.Current.Id.Name;
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or TypeLoadException)
            {
                // InvalidOperationException is what the projection raises when unpackaged.
                return false;
            }
        }
    }

    public static string? Name => Read(p => p.Id.Name);

    public static string? FamilyName => Read(p => p.Id.FamilyName);

    public static string? Version => Read(p =>
    {
        var v = p.Id.Version;
        return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
    });

    public static string? InstallLocation => Read(p => p.InstalledLocation.Path);

    static string? Read(Func<Windows.ApplicationModel.Package, string> read)
    {
        try
        {
            return read(Windows.ApplicationModel.Package.Current);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or TypeLoadException or IOException)
        {
            return null;
        }
    }
}
