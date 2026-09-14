namespace EntreePrintTray;

public sealed class PortsForm : Form
{
    private readonly string _configPath;
    private readonly PluginConfig _sourceConfig;
    private readonly TextBox _bindAddress = new();
    private readonly NumericUpDown _httpPort = NumberBox();
    private readonly NumericUpDown _discoveryPort = NumberBox();

    public event EventHandler<PluginConfig>? ConfigSaved;

    public PortsForm(PluginConfig config, string configPath)
    {
        _sourceConfig = config;
        _configPath = configPath;
        Text = "ENTREE Print Plugin Ports";
        Icon = PrinterIconFactory.CreatePrinterIcon();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(460, 230);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(14)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(panel, "Bind address", _bindAddress);
        AddRow(panel, "Plugin port", _httpPort);
        AddRow(panel, "Discovery UDP port", _discoveryPort);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(14),
            Height = 64
        };
        var save = new Button { Text = "Save", Width = 110, Height = 34 };
        var close = new Button { Text = "Close", Width = 110, Height = 34 };
        buttons.Controls.Add(save);
        buttons.Controls.Add(close);

        Controls.Add(panel);
        Controls.Add(buttons);

        save.Click += async (_, _) =>
        {
            save.Enabled = false; panel.Enabled = false;
            try { await SaveAsync(); }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                if (!IsDisposed) MessageBox.Show(error.Message, "ENTREE Print Plugin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { if (!IsDisposed) { save.Enabled = true; panel.Enabled = true; } }
        };
        close.Click += (_, _) => Close();

        LoadConfig(config);
    }

    private void LoadConfig(PluginConfig config)
    {
        _bindAddress.Text = config.BindAddress;
        _httpPort.Value = config.HttpPort;
        _discoveryPort.Value = config.DiscoveryPort;
    }

    private async Task SaveAsync()
    {
        var baseline = File.Exists(_configPath) ? PluginConfig.Load(_configPath) : _sourceConfig;
        var config = baseline with
        {
            BindAddress = EntreePrint.Configuration.NetworkConfiguration.NormalizeBindAddress(_bindAddress.Text),
            HttpPort = (int)_httpPort.Value,
            DiscoveryPort = (int)_discoveryPort.Value
        };

        await SettingsWriter.SaveAsync(config, _configPath);
        if (!IsDisposed) ConfigSaved?.Invoke(this, config);
    }

    private static NumericUpDown NumberBox()
    {
        return new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Width = 160
        };
    }

    private static void AddRow(TableLayoutPanel panel, string label, Control control)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var labelControl = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 6, 8, 6)
        };
        control.Dock = DockStyle.Top;
        control.Margin = new Padding(0, 4, 0, 4);
        panel.Controls.Add(labelControl, 0, row);
        panel.Controls.Add(control, 1, row);
    }
}
