using Microsoft.Win32;

namespace EntreePrintTray;

public static class TrayStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EntreePrintTray";

    public static string BuildCommand(string executablePath) => $"\"{Path.GetFullPath(executablePath)}\" --background";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(ValueName) as string, BuildCommand(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, BuildCommand(Application.ExecutablePath));
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
