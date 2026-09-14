using System.Net.Http.Json;
using System.Text.Json;

namespace EntreePrintTray;

public sealed class DiagnosticsForm : Form
{
    private readonly PluginConfig _config;
    private readonly TextBox _output = new();
    private readonly Button _refresh = new();

    public DiagnosticsForm(PluginConfig config)
    {
        _config = config;
        Text = "ENTREE Print Plugin Diagnostics";
        Icon = PrinterIconFactory.CreatePrinterIcon();
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        Size = new Size(840, 640);

        _output.Multiline = true;
        _output.ScrollBars = ScrollBars.Both;
        _output.ReadOnly = true;
        _output.Dock = DockStyle.Fill;
        _output.Font = new Font(FontFamily.GenericMonospace, 9);

        _refresh.Text = "Refresh";
        _refresh.Width = 110;
        _refresh.Height = 34;
        _refresh.Click += async (_, _) => await RefreshAsync();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(10),
            FlowDirection = FlowDirection.LeftToRight
        };
        buttons.Controls.Add(_refresh);

        Controls.Add(_output);
        Controls.Add(buttons);
        Shown += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _refresh.Enabled = false;
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5),
                BaseAddress = _config.ServiceUri
            };

            var health = await client.GetFromJsonAsync<JsonElement>("/api/health");
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.AccessToken);
            client.DefaultRequestHeaders.Add("X-Entree-Service-ID", health.GetProperty("serviceId").GetString());
            var printers = await client.GetFromJsonAsync<JsonElement>("/api/printers?refresh=true");
            var payload = new
            {
                serviceUrl = client.BaseAddress?.ToString(),
                health,
                printers
            };
            _output.Text = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception error)
        {
            _output.Text = error.ToString();
        }
        finally
        {
            _refresh.Enabled = true;
        }
    }
}
