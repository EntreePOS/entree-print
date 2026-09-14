using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EntreePrint.Security;

namespace EntreePrintTray;

public sealed record PluginConfig
{
    internal Uri ServiceUri => EntreePrint.Configuration.NetworkConfiguration.ServiceUri(BindAddress, HttpPort, localClient: true);
    internal bool IsLanAccessible => EntreePrint.Configuration.NetworkConfiguration.IsLanAccessible(BindAddress);
    internal PluginConfig WithLanAccess(bool enabled) => this with
    {
        BindAddress = EntreePrint.Configuration.NetworkConfiguration.ApplyLanPreference(BindAddress, enabled)
    };

    private PluginConfig Validate() => this with
    {
        BindAddress = EntreePrint.Configuration.NetworkConfiguration.NormalizeBindAddress(BindAddress),
        HttpPort = EntreePrint.Configuration.NetworkConfiguration.ValidatePort(HttpPort, "Plugin port"),
        DiscoveryPort = EntreePrint.Configuration.NetworkConfiguration.ValidatePort(DiscoveryPort, "Discovery port"),
        PrinterStatusRefreshSeconds = Range(PrinterStatusRefreshSeconds, 1, 3600, "Printer status refresh"),
        PrintRetryMaxAttempts = Range(PrintRetryMaxAttempts, 0, 10000, "Print retry attempts"),
        PrintRetryDelayMs = Range(PrintRetryDelayMs, 0, 600000, "Print retry delay (ms)")
    };

    private static int Range(int value, int minimum, int maximum, string name) => value >= minimum && value <= maximum
        ? value : throw new ArgumentException($"{name} must be between {minimum} and {maximum}.");
    public string BindAddress { get; set; } = "0.0.0.0";
    public int HttpPort { get; set; } = 9779;
    public int DiscoveryPort { get; set; } = 9778;
    public bool AutoInstallWindowsService { get; set; } = true;
    public string CorsAllowedOrigins { get; set; } = "*";
    public string AccessToken { get; set; } = "";
    public string BrowserExecutablePath { get; set; } = "";
    public int PrinterStatusRefreshSeconds { get; set; } = 5;
    public int PrintRetryMaxAttempts { get; set; } = 120;
    public int PrintRetryDelayMs { get; set; } = 5000;
    // Preserve service-only settings when saving this form.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public static string GenerateAccessToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public PluginConfig ResetPreferences() => new()
    {
        AccessToken = AccessToken,
        AdditionalSettings = AdditionalSettings
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "EntreePrintPlugin",
        "entree-print-settings.json");

    public static PluginConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new PluginConfig();
        }

        var loaded = JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(path), JsonOptions)
            ?? new PluginConfig();
        return loaded.Validate();
    }

    public void Save(string path)
    {
        var validated = Validate();
        var installed = string.Equals(Path.GetFullPath(path), DefaultPath, StringComparison.OrdinalIgnoreCase);
        if (installed)
        {
            InitializeInstalledStorage();
            if (File.Exists(path)) ProtectedStorage.AssertTrustedPath(path);
        }
        else Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = installed
                ? new FileInfo(temporary).Create(FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, ProtectedStorage.ConfigurationPermissions())
                : new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, validated, JsonOptions);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal byte[] ToValidatedJson() => JsonSerializer.SerializeToUtf8Bytes(Validate(), JsonOptions);

    internal static void InitializeInstalledStorage()
    {
        if (!ProtectedStorage.IsAdministrator()) throw new UnauthorizedAccessException("Administrator approval is required to save service settings.");
        var directory = Path.GetDirectoryName(DefaultPath)!;
        ProtectedStorage.EnsureDirectory(directory, privateData: false);
        ProtectedStorage.EnsureDirectory(Path.Combine(directory, "spool"), privateData: true);
        if (File.Exists(DefaultPath)) ProtectedStorage.AssertTrustedPath(DefaultPath);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

}
