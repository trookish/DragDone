using Microsoft.Win32;

namespace DragDone.Shell;

/// <summary>Start with Windows through the per-user Run key (no admin, no scheduled task).</summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "DragDone";

    static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) is string s && s.Length > 0;
    }

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            if (key.GetValue(Name) as string != Command) key.SetValue(Name, Command);
        }
        else if (key.GetValue(Name) is not null)
        {
            key.DeleteValue(Name, false);
        }
    }
}
