using Microsoft.Win32;

namespace DragDone.Shell;

/// <summary>Start with Windows: a Run key for the installer build, a startup task for the Store build.</summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "DragDone";

    /// <summary>Must match the TaskId in the package manifest.</summary>
    const string TaskId = "DragDone";

    static string Command => $"\"{Environment.ProcessPath}\" --tray";

    /// <summary>The Run key. False in a packaged app, where the write is virtualised and never read.</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) is string s && s.Length > 0;
    }

    public static void Apply(bool enabled)
    {
        if (PackageInfo.IsPackaged)
        {
            ApplyPackaged(enabled);
            return;
        }
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

    /// <summary>
    /// WinRT calls are async and must not be awaited on the UI thread during startup, so this runs
    /// off-thread and lets Windows settle: Task Manager's Startup tab is the user's control.
    /// </summary>
    static void ApplyPackaged(bool enabled) => _ = Task.Run(async () =>
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                if (task.State == Windows.ApplicationModel.StartupTaskState.Disabled)
                    await task.RequestEnableAsync();
            }
            else if (task.State is Windows.ApplicationModel.StartupTaskState.Enabled
                     or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy)
            {
                task.Disable();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Packaged without the declared startup task, or a policy blocked it. Not fatal.
            Core.Diag.Log($"startup task: {ex.Message}");
        }
    });

    /// <summary>One line describing how startup is registered, for <c>--identity</c> and bug reports.</summary>
    public static string Describe()
    {
        if (!PackageInfo.IsPackaged) return IsEnabled() ? "runkey=present" : "runkey=absent";
        try
        {
            var op = Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
            var done = Task.Run(async () => await op).Wait(TimeSpan.FromSeconds(3));
            return done ? $"startupTask={op.GetResults().State}" : "startupTask=(no answer)";
        }
        catch (Exception ex) when (ex is AggregateException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            return $"startupTask=(unavailable: {ex.GetBaseException().Message})";
        }
    }
}
