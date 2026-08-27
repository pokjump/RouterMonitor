using Microsoft.Win32;

namespace RouterMonitor.Wpf.Services;

/// <summary>Registers the running executable to launch at Windows sign-in, via the per-user Run key (no admin rights needed).</summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RouterMonitor";

    public static void EnsureRegistered()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return;

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

        var command = $"\"{exePath}\"";
        if (key.GetValue(ValueName) as string != command)
            key.SetValue(ValueName, command);
    }
}
