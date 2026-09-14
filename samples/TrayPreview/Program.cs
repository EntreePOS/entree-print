using System.Text.Json;
using EntreePrintTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var instance = SingleInstanceGuard.TryAcquire(@"Local\EntreePrint.TrayDesktopPreview");
        if (!instance.IsPrimary) { instance.RequestActivation(); return; }
        var directory = Path.Combine(Path.GetTempPath(), "EntreeTrayDesktopPreview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "settings.json");
        new PluginConfig { AutoInstallWindowsService = false }.Save(configPath);
        Environment.SetEnvironmentVariable("ENTREE_PRINT_CONFIG", configPath);
        File.WriteAllText(Path.Combine(directory, "preview.json"), JsonSerializer.Serialize(new
        {
            processId = Environment.ProcessId, startedAt = DateTimeOffset.UtcNow, configPath,
            runtime = Environment.Version.ToString(), serviceSetupEnabled = false
        }));
        // Actual tray/settings implementation, isolated from the installed configuration and mutex.
        Application.Run(new TrayApplicationContext(instance, showSettings: true));
    }
}
