namespace EntreePrintTray;

public sealed class SettingsForm : Form
{
    private readonly string _configPath;
    private PluginConfig _configSnapshot;
    private readonly ToolTip _toolTip = new();
    private readonly CheckBox _localNetworkAccessible = new();
    private readonly NumericUpDown _httpPort = NumberBox(1, 65535);
    private readonly NumericUpDown _discoveryPort = NumberBox(1, 65535);
    private readonly CheckBox _autoInstallWindowsService = new();
    private readonly CheckBox _trayStartup = new();
    private readonly TextBox _corsAllowedOrigins = new();
    private readonly TextBox _apiAccessToken = new();
    private readonly CheckBox _showAccessToken = new();
    private readonly Button _generateAccessToken = new();
    private readonly TextBox _browserPath = new();
    private readonly NumericUpDown _printerStatusRefreshSeconds = NumberBox(1, 3600);
    private readonly NumericUpDown _printRetryMaxAttempts = NumberBox(0, 10000);
    private readonly NumericUpDown _printRetryDelaySeconds = NumberBox(1, 600);

    public event EventHandler<PluginConfig>? ConfigSaved;

    public SettingsForm(PluginConfig config, string configPath)
    {
        _configPath = configPath;
        _configSnapshot = config;
        Text = "ENTREE Print Plugin Settings";
        Icon = PrinterIconFactory.CreatePrinterIcon();
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(440, 460);
        Size = new Size(440, 580);

        _apiAccessToken.ReadOnly = true;
        _apiAccessToken.UseSystemPasswordChar = true;
        _apiAccessToken.Font = new Font(FontFamily.GenericMonospace, 9);
        _generateAccessToken.Width = 132;
        _generateAccessToken.Height = 30;
        _generateAccessToken.Click += (_, _) =>
        {
            _apiAccessToken.Text = PluginConfig.GenerateAccessToken();
            _generateAccessToken.Text = "Replace token";
        };
        _showAccessToken.CheckedChanged += (_, _) => _apiAccessToken.UseSystemPasswordChar = !_showAccessToken.Checked;

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 6)
        };

        var basicPanel = CreateSettingsPanel();
        AddRow(basicPanel, "Tray starts with Windows", _trayStartup);
        _trayStartup.Checked = TrayStartupRegistration.IsEnabled();
        AddRow(basicPanel, "Install/start print service", _autoInstallWindowsService);
        AddRow(basicPanel, "Local Network Accessible", _localNetworkAccessible);
        AddSpacer(basicPanel);
        AddRow(basicPanel, "Plugin port", _httpPort, "The port your POS uses to connect to this print service.");
        AddRow(basicPanel, "Discovery port", _discoveryPort, "The UDP port tablets use to automatically find this print plugin on the local network.");

        AddNote(basicPanel, "Set paper size, print quality and cutting in your printer's Windows Printing Preferences.");

        var securityPanel = CreateSettingsPanel();
        AddRow(securityPanel, "POS website address", _corsAllowedOrigins, "Leave empty to block access from other websites. This setting applies to web browsers.");
        AddNote(securityPanel, "The website address you use to open your POS.");
        AddNote(securityPanel, "For example, if you open:\nhttps://pos.example.com/orders\nEnter: https://pos.example.com");
        AddNote(securityPanel, "* means any website. For more than one address, separate them with commas.");
        AddSpacer(securityPanel);
        AddRow(securityPanel, "API access token", _apiAccessToken);
        AddRow(securityPanel, "Show token", _showAccessToken);
        AddRow(securityPanel, "Token management", _generateAccessToken);
        AddNote(securityPanel, "Generate a token and enter it in the POS configuration. Save and restart the print service to apply it. Replacing the token requires updating every connected POS.");
        AddNote(securityPanel, "The website address controls access from a web browser. Your POS also needs the API token above.");

        var advancedPanel = CreateSettingsPanel();
        AddRow(advancedPanel, "Browser path", _browserPath);
        AddRow(advancedPanel, "Refresh interval (s)", _printerStatusRefreshSeconds);
        AddRow(advancedPanel, "Retry count", _printRetryMaxAttempts);
        AddRow(advancedPanel, "Retry delay (s)", _printRetryDelaySeconds);

        tabs.TabPages.Add(CreateTab("Basic", basicPanel));

        tabs.TabPages.Add(CreateTab("Security", securityPanel));
        tabs.TabPages.Add(CreateTab("Advanced", advancedPanel));

        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Padding = new Padding(14),
            Height = 64,
            ColumnCount = 2
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 164));

        var leftButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        var rightButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0)
        };
        var save = new Button { Text = "Save", Width = 76, Height = 34 };
        var resetDefault = new Button { Text = "Reset Default", Width = 108, Height = 34 };
        var close = new Button { Text = "Close", Width = 76, Height = 34 };
        _toolTip.SetToolTip(resetDefault, "Restore default preferences and keep your API access token. Click Save to apply.");
        leftButtons.Controls.Add(resetDefault);
        rightButtons.Controls.Add(save);
        rightButtons.Controls.Add(close);
        buttons.Controls.Add(leftButtons, 0, 0);
        buttons.Controls.Add(rightButtons, 1, 0);

        Controls.Add(tabs);
        Controls.Add(buttons);

        save.Click += (_, _) =>
        {
            try { Save(); }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "Unable to save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        resetDefault.Click += (_, _) =>
        {
            LoadConfig((_configSnapshot with { AccessToken = _apiAccessToken.Text }).ResetPreferences());
            _trayStartup.Checked = false;
        };
        close.Click += (_, _) => Close();

        LoadConfig(config);
    }

    private void LoadConfig(PluginConfig config)
    {
        _configSnapshot = config;
        _localNetworkAccessible.Checked = config.IsLanAccessible;
        _httpPort.Value = config.HttpPort;
        _discoveryPort.Value = config.DiscoveryPort;
        _autoInstallWindowsService.Checked = config.AutoInstallWindowsService;
        _corsAllowedOrigins.Text = config.CorsAllowedOrigins;
        _apiAccessToken.Text = config.AccessToken;
        _showAccessToken.Checked = false;
        _generateAccessToken.Text = string.IsNullOrEmpty(config.AccessToken) ? "Generate token" : "Replace token";
        _browserPath.Text = config.BrowserExecutablePath;
        _printerStatusRefreshSeconds.Value = config.PrinterStatusRefreshSeconds;
        _printRetryMaxAttempts.Value = config.PrintRetryMaxAttempts;
        _printRetryDelaySeconds.Value = Math.Max(1, config.PrintRetryDelayMs / 1000);
    }

    private void Save()
    {
        var config = _configSnapshot.WithLanAccess(_localNetworkAccessible.Checked) with
        {
            HttpPort = (int)_httpPort.Value,
            DiscoveryPort = (int)_discoveryPort.Value,
            AutoInstallWindowsService = _autoInstallWindowsService.Checked,
            CorsAllowedOrigins = EntreePrint.Configuration.SecurityConfiguration.NormalizeOrigins(_corsAllowedOrigins.Text),
            AccessToken = _apiAccessToken.Text,
            BrowserExecutablePath = _browserPath.Text.Trim(),
            PrinterStatusRefreshSeconds = (int)_printerStatusRefreshSeconds.Value,
            PrintRetryMaxAttempts = (int)_printRetryMaxAttempts.Value,
            PrintRetryDelayMs = (int)_printRetryDelaySeconds.Value * 1000
        };

        config.Save(_configPath);
        _configSnapshot = config;
        TrayStartupRegistration.SetEnabled(_trayStartup.Checked);
        ConfigSaved?.Invoke(this, config);
    }

    private static NumericUpDown NumberBox(int minimum, int maximum)
    {
        return new NumericUpDown
        {
            Minimum = minimum,
            Maximum = maximum,
            Width = 84,
            TextAlign = HorizontalAlignment.Right
        };
    }

    internal static void ConfigureCommandInput(TextBox input)
    {
        input.CharacterCasing = CharacterCasing.Upper;
        input.Font = new Font(FontFamily.GenericMonospace, 9);
        var formatting = false;
        input.TextChanged += (_, _) =>
        {
            if (formatting) return;
            var original = input.Text;
            var start = original.Take(input.SelectionStart).Count(character => !char.IsWhiteSpace(character));
            var end = original.Take(input.SelectionStart + input.SelectionLength).Count(character => !char.IsWhiteSpace(character));
            var characters = original.Where(character => !char.IsWhiteSpace(character)).ToArray();
            var formatted = string.Join(" ", characters.Chunk(2).Select(pair => new string(pair)));
            if (original == formatted) return;
            formatting = true;
            try
            {
                input.Text = formatted;
                var selectionStart = Math.Min(formatted.Length, start + start / 2);
                var selectionEnd = Math.Min(formatted.Length, end + end / 2);
                input.Select(selectionStart, selectionEnd - selectionStart);
            }
            finally { formatting = false; }
        };
        input.KeyDown += (_, args) =>
        {
            // Delete digits across an inserted separator instead of repeatedly recreating the space.
            if (input.SelectionLength != 0) return;
            var position = input.SelectionStart;
            if (args.KeyData == Keys.Back && position > 0 && input.Text[position - 1] == ' ')
                input.SelectionStart = position - 1;
            else if (args.KeyData == Keys.Delete && position < input.TextLength && input.Text[position] == ' ')
                input.SelectionStart = position + 1;
        };
    }

    private static TableLayoutPanel CreateSettingsPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(14)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        return panel;
    }

    private static TabPage CreateTab(string text, Control content)
    {
        var tab = new TabPage(text)
        {
            Padding = new Padding(0)
        };
        var scrollContainer = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };
        scrollContainer.Controls.Add(content);
        tab.Controls.Add(scrollContainer);
        return tab;
    }

    private void AddRow(TableLayoutPanel panel, string label, Control control, string helpText = "")
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var labelControl = CreateLabel(label, helpText);
        control.AccessibleName = label;
        control.Anchor = control is TextBox
            ? AnchorStyles.Left | AnchorStyles.Right
            : AnchorStyles.Right;
        control.Margin = new Padding(0, 4, 0, 4);
        if (control is CheckBox checkBox)
        {
            checkBox.AutoSize = true;
            checkBox.Margin = new Padding(0, 8, 0, 8);
        }
        if (control is ComboBox) control.Width = 164;
        panel.Controls.Add(labelControl, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    private Control CreateLabel(string label, string helpText)
    {
        if (string.IsNullOrWhiteSpace(helpText))
        {
            return new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 6, 8, 6)
            };
        }

        var container = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0, 6, 8, 6)
        };
        var labelControl = new Label
        {
            Text = label,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 2, 5, 0)
        };
        var help = new Label
        {
            Text = "?",
            AutoSize = false,
            Width = 18,
            Height = 18,
            TextAlign = ContentAlignment.MiddleCenter,
            BorderStyle = BorderStyle.FixedSingle,
            Cursor = Cursors.Help,
            Margin = new Padding(0, 0, 0, 0)
        };
        _toolTip.SetToolTip(help, helpText);
        _toolTip.SetToolTip(labelControl, helpText);
        container.Controls.Add(labelControl);
        container.Controls.Add(help);
        return container;
    }

    private static void AddSpacer(TableLayoutPanel panel)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
        panel.Controls.Add(new Label { Height = 10 }, 0, row);
        panel.Controls.Add(new Label { Height = 10 }, 1, row);
    }

    private static void AddNote(TableLayoutPanel panel, string text)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var note = new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 0, 8) };
        // Constrain the label to the current panel width so AutoSize wraps at narrow window sizes.
        panel.SizeChanged += (_, _) => note.MaximumSize = new Size(Math.Max(100, panel.ClientSize.Width - panel.Padding.Horizontal), 0);
        note.MaximumSize = new Size(360, 0);
        panel.Controls.Add(note, 0, row);
        panel.SetColumnSpan(note, 2);
    }


}
