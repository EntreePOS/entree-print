namespace EntreePrintTray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _printerIcon = PrinterIconFactory.CreatePrinterIcon();
    private readonly WindowsServiceManager _serviceManager = new();
    private readonly string _configPath;
    private PluginConfig _config;
    private SettingsForm? _settingsForm;
    private PortsForm? _portsForm;
    private DiagnosticsForm? _diagnosticsForm;
    private readonly System.Windows.Forms.Timer _activationTimer = new() { Interval = 250 };
    private ToolStripMenuItem? _serviceMenu;
    private ToolStripItem? _exitMenu;
    private bool _serviceActionRunning;
    private bool _disposed;

    public TrayApplicationContext(SingleInstanceGuard singleInstance, bool showSettings)
        : this(singleInstance, showSettings, Environment.GetEnvironmentVariable("ENTREE_PRINT_CONFIG") ?? PluginConfig.DefaultPath)
    {
    }

    internal TrayApplicationContext(SingleInstanceGuard singleInstance, bool showSettings, string configPath)
    {
        _configPath = configPath;
        string? configurationError = null;
        try { _config = PluginConfig.Load(_configPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            _config = new PluginConfig { AutoInstallWindowsService = false };
            configurationError = $"Settings could not be read. Automatic service setup was skipped. {error.Message}";
        }
        // Opening the tray is read-only. A missing or unwritable configuration folder
        // must not prevent the icon from appearing; Settings saves the first configuration.

        _notifyIcon = new NotifyIcon
        {
            Icon = _printerIcon,
            Text = "ENTREE Print Plugin",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _notifyIcon.DoubleClick += (_, _) => RunMenuAction(ShowSettings);
        var firstTick = true;
        _activationTimer.Tick += async (_, _) =>
        {
            if (singleInstance.ConsumeActivation()) RunMenuAction(ShowSettings);
            if (!firstTick) return;
            firstTick = false;
            if (showSettings) RunMenuAction(ShowSettings);
            if (configurationError is not null)
                _notifyIcon.ShowBalloonTip(5000, "ENTREE Print Plugin", configurationError, ToolTipIcon.Warning);
            else if (File.Exists(_configPath)) await TryEnsureWindowsServiceAsync(showDisabledMessage: false);
            else _notifyIcon.ShowBalloonTip(3500, "ENTREE Print Plugin", "Open Settings and save your setup to enable printing.", ToolTipIcon.Info);
        };
        _activationTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            _activationTimer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _printerIcon.Dispose();
            _settingsForm?.Dispose();
            _portsForm?.Dispose();
            _diagnosticsForm?.Dispose();
        }
        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => RunMenuAction(ShowSettings));
        menu.Items.Add("Ports...", null, (_, _) => RunMenuAction(ShowPorts));
        menu.Items.Add("Diagnostics", null, (_, _) => RunMenuAction(ShowDiagnostics));
        menu.Items.Add("Open Config Folder", null, (_, _) => RunMenuAction(OpenConfigFolder));
        menu.Items.Add(BuildServiceMenu());
        menu.Items.Add(new ToolStripSeparator());
        _exitMenu = menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private ToolStripMenuItem BuildServiceMenu()
    {
        var service = _serviceMenu = new ToolStripMenuItem("Windows Service");
        service.DropDownItems.Add("Install Service", null, async (_, _) => await RunServiceActionAsync("installing service",
            _serviceManager.Install));
        service.DropDownItems.Add("Uninstall Service", null, async (_, _) => await RunServiceActionAsync("uninstalling service", _serviceManager.Uninstall));
        service.DropDownItems.Add(new ToolStripSeparator());
        service.DropDownItems.Add("Start Service", null, async (_, _) => await RunServiceActionAsync("starting service",
            _serviceManager.Start));
        service.DropDownItems.Add("Stop Service", null, async (_, _) => await RunServiceActionAsync("stopping service", _serviceManager.Stop));
        service.DropDownItems.Add("Restart Service", null, async (_, _) => await RunServiceActionAsync("restarting service",
            _serviceManager.Restart));
        return service;
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            if (_settingsForm.WindowState == FormWindowState.Minimized) _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.Show();
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_config, _configPath);
        _settingsForm.Icon = _printerIcon;
        _settingsForm.ConfigSaved += async (_, config) =>
        {
            _config = config;
            await PromptToApplySavedConfigAsync();
        };
        _settingsForm.Show();
    }

    internal void ShowPorts()
    {
        if (_portsForm is { IsDisposed: false })
        {
            _portsForm.Activate();
            return;
        }

        _config = PluginConfig.Load(_configPath);
        _portsForm = new PortsForm(_config, _configPath);
        _portsForm.Icon = _printerIcon;
        _portsForm.ConfigSaved += async (_, config) =>
        {
            _config = config;
            await PromptToApplySavedConfigAsync();
        };
        _portsForm.Show();
    }

    private Task PromptToApplySavedConfigAsync()
    {
        if (_serviceActionRunning)
        {
            _notifyIcon.ShowBalloonTip(3500, "ENTREE Print Plugin",
                "Settings saved. Wait for the current service operation to finish, then restart the service to apply them.", ToolTipIcon.Info);
            return Task.CompletedTask;
        }
        var config = _config;
        return RunServiceOperationAsync(async () =>
        {
            var installed = await Task.Run(_serviceManager.IsInstalled);
            if (_disposed) return null;
            if (!installed)
                return config.AutoInstallWindowsService
                    ? await Task.Run(() => EnsureWindowsService())
                    : "Settings saved. Automatic Windows Service install is disabled.";
            var result = MessageBox.Show(
                "Settings saved. Restart the Windows Service now to apply them?",
                "ENTREE Print Plugin", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result != DialogResult.Yes)
                return "Settings saved. Restart the service later to apply runtime changes.";
            await Task.Run(_serviceManager.Restart);
            return "Windows Service restarted with saved settings.";
        }, showErrorDialog: true);
    }

    private Task TryEnsureWindowsServiceAsync(bool showDisabledMessage)
    {
        if (!_config.AutoInstallWindowsService)
        {
            if (showDisabledMessage)
            {
                _notifyIcon.ShowBalloonTip(2500, "ENTREE Print Plugin", "Settings saved. Automatic Windows Service install is disabled.", ToolTipIcon.Info);
            }
            return Task.CompletedTask;
        }
        return RunServiceOperationAsync(() => Task.Run(EnsureWindowsService));
    }

    private string? EnsureWindowsService()
    {
        if (_serviceManager.IsInstalled())
        {
            if (_serviceManager.IsRunning()) return null;
            _serviceManager.Start();
            return "Windows Service is running.";
        }
        _serviceManager.Install();
        return "Windows Service installed and started.";
    }

    internal void ShowDiagnostics()
    {
        if (_diagnosticsForm is { IsDisposed: false })
        {
            _diagnosticsForm.Activate();
            return;
        }

        _config = PluginConfig.Load(_configPath);
        _diagnosticsForm = new DiagnosticsForm(_config);
        _diagnosticsForm.Icon = _printerIcon;
        _diagnosticsForm.Show();
    }

    internal Task RunServiceActionAsync(string actionName, Action action) =>
        RunServiceOperationAsync(() => Task.Run(() =>
        {
            action();
            return (string?)$"Finished {actionName}.";
        }), showErrorDialog: true);

    internal bool RunMenuAction(Action action)
    {
        if (_disposed) return false;
        try { action(); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or ArgumentException or System.ComponentModel.Win32Exception)
        {
            _notifyIcon.ShowBalloonTip(5000, "ENTREE Print Plugin", error.Message, ToolTipIcon.Error);
            return false;
        }
    }

    private async Task RunServiceOperationAsync(Func<Task<string?>> action, bool showErrorDialog = false)
    {
        if (_disposed || _serviceActionRunning) return;
        _serviceActionRunning = true;
        if (_serviceMenu is not null) _serviceMenu.Enabled = false;
        if (_exitMenu is not null) _exitMenu.Enabled = false;
        _notifyIcon.Text = "ENTREE Print Plugin — service setup in progress";
        try
        {
            var message = await action();
            if (!_disposed && message is not null)
                _notifyIcon.ShowBalloonTip(3500, "ENTREE Print Plugin", message, ToolTipIcon.Info);
        }
        catch (Exception error)
        {
            if (!_disposed)
            {
                _notifyIcon.ShowBalloonTip(5000, "ENTREE Print Plugin", error.Message, ToolTipIcon.Error);
                if (showErrorDialog)
                    MessageBox.Show(error.Message, "ENTREE Print Plugin", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            _serviceActionRunning = false;
            if (!_disposed)
            {
                if (_serviceMenu is not null) _serviceMenu.Enabled = true;
                if (_exitMenu is not null) _exitMenu.Enabled = true;
                _notifyIcon.Text = "ENTREE Print Plugin";
            }
        }
    }

    private void OpenConfigFolder()
    {
        var directory = Path.GetDirectoryName(_configPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
    }
}
