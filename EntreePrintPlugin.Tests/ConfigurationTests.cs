using System.Text;
using EntreePrintTray;
using Microsoft.Extensions.Configuration;

namespace EntreePrintPlugin.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void SavedTrayConfiguration_OverridesBundledDefaults_AndRetainsServiceSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EntreeConfigurationTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var token = PluginConfig.GenerateAccessToken();
            new PluginConfig { BindAddress = "127.0.0.1", HttpPort = 19779, DiscoveryPort = 19778,
                CorsAllowedOrigins = "https://POS.example.com:8443/", AccessToken = token,
                AdditionalSettings = new() { ["RequestTimeoutMs"] = System.Text.Json.JsonSerializer.SerializeToElement(2500) },
                PrinterStatusRefreshSeconds = 9 }.Save(path);
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
                .AddJsonFile(path).Build();
            var settings = PluginSettings.FromConfiguration(configuration);
            Assert.Equal("127.0.0.1", settings.BindAddress);
            Assert.Equal(19779, settings.HttpPort);
            Assert.Equal(19778, settings.DiscoveryPort);
            Assert.Equal("https://pos.example.com:8443", settings.CorsAllowedOrigins);
            Assert.Equal(token, settings.AccessToken);
            Assert.Equal(2500, settings.RequestTimeoutMs);
            Assert.Equal(9, settings.PrinterStatusRefreshSeconds);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExplicitDeploymentOverrides_WinOverTrayAndBundledSettings()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"Plugin":{"HttpPort":9779,"RequestTimeoutMs":3000},
             "HttpPort":19779,"RequestTimeoutMs":2500,
             "HTTP_PORT":"29779","API_REQUEST_TIMEOUT_MS":"1500"}
            """));
        var settings = PluginSettings.FromConfiguration(new ConfigurationBuilder().AddJsonStream(input).Build());
        Assert.Equal(29779, settings.HttpPort);
        Assert.Equal(1500, settings.RequestTimeoutMs);
    }

    [Fact]
    public void ResetPreferences_PreservesCredentialsAndProfiles()
    {
        var original = new PluginConfig { AccessToken = PluginConfig.GenerateAccessToken(),
            HttpPort = 19779,
            AdditionalSettings = new() { ["PrinterProfiles"] = System.Text.Json.JsonSerializer.SerializeToElement(new { Kitchen = new { BeepCommandHex = "1B 42 03 01" } }) } };
        var reset = original.ResetPreferences();
        Assert.Equal(original.AccessToken, reset.AccessToken);
        Assert.Equal("1B 42 03 01", reset.AdditionalSettings!["PrinterProfiles"].GetProperty("Kitchen").GetProperty("BeepCommandHex").GetString());
        Assert.Equal(9779, reset.HttpPort);
        Assert.Equal(19779, original.HttpPort);
        Assert.Matches("^[0-9a-f]{64}$", original.AccessToken);
        Assert.NotEqual(original.AccessToken, PluginConfig.GenerateAccessToken());
    }

    [Theory]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("http://localhost:8080,https://pos.example.com")]
    [InlineData("http://[::1]:9779")]
    public void AllowedWebsites_AcceptsOriginsWildcardAndEmpty(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CorsAllowedOrigins"] = value }).Build();
        Assert.Equal(value, PluginSettings.FromConfiguration(configuration).CorsAllowedOrigins);
    }

    [Theory]
    [InlineData("pos.example.com")]
    [InlineData("https://pos.example.com/orders")]
    [InlineData("https://pos.example.com?token=secret")]
    [InlineData("https://pos.example.com#orders")]
    [InlineData("https://user:password@pos.example.com")]
    [InlineData("*,https://pos.example.com")]
    [InlineData("https://*.example.com")]
    [InlineData("file:///C:/POS/index.html")]
    [InlineData("null")]
    public void AllowedWebsites_RejectsAmbiguousOrNonOriginSettings(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CorsAllowedOrigins"] = value }).Build();
        Assert.Throws<ArgumentException>(() => PluginSettings.FromConfiguration(configuration));
    }
}
