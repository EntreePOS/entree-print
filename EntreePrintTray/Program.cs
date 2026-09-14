namespace EntreePrintTray;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        using var singleInstance = SingleInstanceGuard.TryAcquire();
        if (!singleInstance.IsPrimary)
        {
            if (!background) singleInstance.RequestActivation();
            return;
        }

        try
        {
            Application.Run(new TrayApplicationContext(singleInstance, showSettings: !background));
        }
        catch (Exception error)
        {
            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EntreePrintPlugin");
            var logMessage = $"Log: {logDirectory}";
            try
            {
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(Path.Combine(logDirectory, "tray-startup.log"), $"{DateTimeOffset.Now:O} {error}\n");
            }
            catch (Exception loggingError) when (loggingError is IOException or UnauthorizedAccessException)
            {
                logMessage = "The startup log could not be saved.";
            }
            MessageBox.Show($"ENTREE Print could not start its tray application.\n\n{error.Message}\n\n{logMessage}",
                "ENTREE Print", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }    
}
