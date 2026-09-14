namespace EntreePrintPlugin;

public sealed record PluginSettings
{
    public string BindAddress { get; init; } = "0.0.0.0";
    public int HttpPort { get; init; } = 9779;
    public int DiscoveryPort { get; init; } = 9778;
    public string CorsAllowedOrigins { get; init; } = "*";
    public string AccessToken { get; init; } = "";
    public string HttpsCertificateThumbprint { get; init; } = "";
    public string HttpsHost { get; init; } = "";
    internal string Scheme => HttpsCertificateThumbprint.Length == 0 ? "http" : "https";
    public Dictionary<string, Models.PrinterProfile> PrinterProfiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int RetryTimeoutMs { get; init; } = 60000;
    public int RequestTimeoutMs { get; init; } = 3000;
    public static string DefaultSpoolPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EntreePrintPlugin", "spool");
    public string SpoolPath { get; init; } = DefaultSpoolPath;
    public string BrowserExecutablePath { get; init; } = "";
    public int PrinterStatusRefreshSeconds { get; init; } = 5;
    public int PrintRetryMaxAttempts { get; init; } = 120;
    public int PrintRetryDelayMs { get; init; } = 5000;
    public int ReceiptRetentionDays { get; init; } = 7;
    internal Uri ListenUri => EntreePrint.Configuration.NetworkConfiguration.ServiceUri(BindAddress, HttpPort, httpsHost: HttpsHost);

    public static PluginSettings FromConfiguration(IConfiguration configuration)
    {
        var https = EntreePrint.Configuration.NetworkConfiguration.HttpsSettings(
            ReadString(configuration, "HTTPS_CERTIFICATE_THUMBPRINT", "Plugin:HttpsCertificateThumbprint", "HttpsCertificateThumbprint", ""),
            ReadString(configuration, "HTTPS_HOST", "Plugin:HttpsHost", "HttpsHost", ""));
        return new PluginSettings
        {
            BindAddress = EntreePrint.Configuration.NetworkConfiguration.NormalizeBindAddress(ReadString(configuration, "BIND_ADDRESS", "Plugin:BindAddress", "BindAddress", "0.0.0.0")),
            HttpPort = EntreePrint.Configuration.NetworkConfiguration.ValidatePort(ReadInt(configuration, "HTTP_PORT", "Plugin:HttpPort", "HttpPort", 9779), "Plugin port"),
            DiscoveryPort = EntreePrint.Configuration.NetworkConfiguration.ValidatePort(ReadInt(configuration, "DISCOVERY_PORT", "Plugin:DiscoveryPort", "DiscoveryPort", 9778), "Discovery port"),
            CorsAllowedOrigins = EntreePrint.Configuration.SecurityConfiguration.NormalizeOrigins(ReadString(configuration, "CORS_ALLOWED_ORIGINS", "Plugin:CorsAllowedOrigins", "CorsAllowedOrigins", "*")),
            AccessToken = ReadString(configuration, "ENTREE_PRINT_API_TOKEN", "Plugin:AccessToken", "AccessToken", ""),
            HttpsCertificateThumbprint = https.Thumbprint,
            HttpsHost = https.Host,
            PrinterProfiles = configuration.GetSection("PrinterProfiles").Get<Dictionary<string, Models.PrinterProfile>>() ?? new(StringComparer.OrdinalIgnoreCase),
            RetryTimeoutMs = ReadInt(configuration, "API_RETRY_TIMEOUT_MS", "Plugin:RetryTimeoutMs", "RetryTimeoutMs", 60000),
            RequestTimeoutMs = ReadInt(configuration, "API_REQUEST_TIMEOUT_MS", "Plugin:RequestTimeoutMs", "RequestTimeoutMs", 3000),
            SpoolPath = ResolvePath(ReadString(configuration, "SPOOL_PATH", "Plugin:SpoolPath", "SpoolPath", DefaultSpoolPath)),
            BrowserExecutablePath = ReadString(configuration, "BROWSER_EXECUTABLE_PATH", "Plugin:BrowserExecutablePath", "BrowserExecutablePath", ""),
            PrinterStatusRefreshSeconds = ReadInt(configuration, "PRINTER_STATUS_REFRESH_SECONDS", "Plugin:PrinterStatusRefreshSeconds", "PrinterStatusRefreshSeconds", 5),
            PrintRetryMaxAttempts = ReadInt(configuration, "PRINT_RETRY_MAX_ATTEMPTS", "Plugin:PrintRetryMaxAttempts", "PrintRetryMaxAttempts", 120),
            PrintRetryDelayMs = ReadInt(configuration, "PRINT_RETRY_DELAY_MS", "Plugin:PrintRetryDelayMs", "PrintRetryDelayMs", 5000),
            ReceiptRetentionDays = ReadRetentionDays(configuration)
        };
    }

    private static string ReadString(IConfiguration configuration, string envKey, string configKey, string flatKey, string fallback)
    {
        // The tray writes flat installation settings; Plugin:* contains bundled defaults.
        return configuration[envKey] ?? configuration[flatKey] ?? configuration[configKey] ?? fallback;
    }

    private static int ReadRetentionDays(IConfiguration configuration)
    {
        var days = ReadInt(configuration, "RECEIPT_RETENTION_DAYS", "Plugin:ReceiptRetentionDays", "ReceiptRetentionDays", 7);
        return days is >= 1 and <= 365 ? days : throw new ArgumentException("ReceiptRetentionDays must be between 1 and 365.");
    }

    private static int ReadInt(IConfiguration configuration, string envKey, string configKey, string flatKey, int fallback)
    {
        var value = configuration[envKey] ?? configuration[flatKey] ?? configuration[configKey];
        if (value is null) return fallback;
        if (int.TryParse(value, out var parsed)) return parsed;
        throw new ArgumentException($"{flatKey} must be a whole number.");
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathFullyQualified(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }
}
