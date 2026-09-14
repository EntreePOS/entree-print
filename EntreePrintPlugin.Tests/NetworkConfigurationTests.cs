using EntreePrintTray;
using Microsoft.Extensions.Configuration;

namespace EntreePrintPlugin.Tests;

public sealed class NetworkConfigurationTests
{
    [Theory]
    [InlineData(" localhost ", "127.0.0.1", "http://127.0.0.1:9779/")]
    [InlineData("::1", "::1", "http://[::1]:9779/")]
    [InlineData("192.168.1.20", "192.168.1.20", "http://192.168.1.20:9779/")]
    public void ServiceAndTrayUseValidEndpoints(string input, string normalized, string uri)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["BindAddress"] = input }).Build();
        var settings = PluginSettings.FromConfiguration(config);
        Assert.Equal(normalized, settings.BindAddress);
        Assert.Equal(uri, settings.ListenUri.AbsoluteUri);
        Assert.Equal(uri, new PluginConfig { BindAddress = input }.ServiceUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("0.0.0.0", "http://127.0.0.1:9779/")]
    [InlineData("::", "http://[::1]:9779/")]
    public void DiagnosticsMapsWildcardToLoopback(string bind, string uri) =>
        Assert.Equal(uri, new PluginConfig { BindAddress = bind }.ServiceUri.AbsoluteUri);

    [Theory]
    [InlineData("192.168.1.20", true, "192.168.1.20")]
    [InlineData("::1", false, "::1")]
    [InlineData("localhost", false, "127.0.0.1")]
    [InlineData("192.168.1.20", false, "127.0.0.1")]
    [InlineData("::1", true, "0.0.0.0")]
    public void BasicPreferencesPreserveExplicitAddressUnlessAccessChanges(string bind, bool enabled, string expected) =>
        Assert.Equal(expected, new PluginConfig { BindAddress = bind }.WithLanAccess(enabled).BindAddress);

    [Theory]
    [InlineData("BindAddress", "printer.example.com")]
    [InlineData("HttpPort", "0")]
    [InlineData("DiscoveryPort", "65536")]
    [InlineData("HTTP_PORT", "abc")]
    public void InvalidServiceNetworkSettingsFailExplicitly(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        Assert.Throws<ArgumentException>(() => PluginSettings.FromConfiguration(config));
    }

    [Fact]
    public void NullAddressInSavedJsonReportsConfigurationError()
    {
        var path = Path.Combine(Path.GetTempPath(), "entree-null-bind-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"BindAddress\":null}");
            Assert.Throws<ArgumentException>(() => PluginConfig.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void InvalidTraySavePreservesPreviousFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EntreeNetworkTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            new PluginConfig { BindAddress = "localhost" }.Save(path);
            var original = File.ReadAllBytes(path);
            Assert.Equal("127.0.0.1", PluginConfig.Load(path).BindAddress);
            Assert.Throws<ArgumentException>(() => new PluginConfig { DiscoveryPort = -1 }.Save(path));
            Assert.Throws<ArgumentException>(() => new PluginConfig { BindAddress = "invalid" }.Save(path));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
