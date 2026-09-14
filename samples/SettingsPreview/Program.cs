using EntreePrintTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var config = new PluginConfig { AutoInstallWindowsService = false };
        using var form = new SettingsForm(config, Path.Combine(Path.GetTempPath(), "EntreeSettingsPreview", "settings.json"));
        form.Text = "ENTREE Print Settings — Preview";
        Application.Run(form);
    }
}
