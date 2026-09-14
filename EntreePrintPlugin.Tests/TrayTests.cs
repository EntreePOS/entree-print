using EntreePrintTray;

namespace EntreePrintPlugin.Tests;

public sealed class TrayTests
{
    [Fact]
    public void ConfigSave_PreservesServiceOnlyProfileAndAuthorizationSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EntreeConfigTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            File.WriteAllText(path, """{"AccessToken":"fixture-only-token","PrinterProfiles":{"Kitchen":{"BeepCommandHex":"1B 42 03 01"}}}""");
            var loaded = PluginConfig.Load(path);
            (loaded with { HttpPort = 19779 }).Save(path);
            using var result = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("fixture-only-token", result.RootElement.GetProperty("AccessToken").GetString());
            Assert.Equal("1B 42 03 01", result.RootElement.GetProperty("PrinterProfiles").GetProperty("Kitchen").GetProperty("BeepCommandHex").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public void TrayStartupCommand_QuotesSpacesAndUsesBackgroundMode()
    {
        Assert.Equal("\"C:\\Program Files\\ENTREE Print\\tray\\EntreePrintTray.exe\" --background",
            TrayStartupRegistration.BuildCommand("C:\\Program Files\\ENTREE Print\\tray\\EntreePrintTray.exe"));
    }

    [Fact]
    public void SingleInstanceGuard_SecondLaunchSignalsExistingOwner()
    {
        var name = $@"Local\EntreePrintPlugin.Tests.{Guid.NewGuid():N}";
        using var first = SingleInstanceGuard.TryAcquire(name);
        var thread = new Thread(() =>
        {
            using var second = SingleInstanceGuard.TryAcquire(name);
            Assert.False(second.IsPrimary);
            second.RequestActivation();
        });
        thread.Start();
        thread.Join();
        Assert.True(first.ConsumeActivation());
        Assert.False(first.ConsumeActivation());
    }

    [Fact]
    public void WindowsServiceManager_InstallScriptUsesConfiguredPorts()
    {
        var manager = new WindowsServiceManager();
        var config = new PluginConfig
        {
            HttpPort = 17779,
            DiscoveryPort = 17778
        };

        var script = manager.BuildInstallScript("C:\\Program Files\\EntreePrintPlugin\\service\\EntreePrintPlugin.exe", config);

        Assert.Contains("-LocalPort 17779", script);
        Assert.Contains("-LocalPort 17778", script);
        Assert.Contains("-RemoteAddress LocalSubnet", script);
        Assert.DoesNotContain("localport=9779", script);
        Assert.DoesNotContain("localport=9780", script);
        Assert.DoesNotContain("localport=9778", script);
    }

    [Fact]
    public void WindowsServiceManager_FirewallScriptRemovesLegacyRulesAndAddsCurrentLanPorts()
    {
        var manager = new WindowsServiceManager();
        var config = new PluginConfig
        {
            BindAddress = "0.0.0.0",
            HttpPort = 18779,
            DiscoveryPort = 18778
        };

        var script = manager.BuildFirewallScript(config);

        Assert.Contains("'ENTREE Print Plugin HTTP'", script);
        Assert.Contains("'ENTREE Print Plugin TCP SDK'", script);
        Assert.Contains("'ENTREE Print Plugin Discovery'", script);
        Assert.Contains("Remove-NetFirewallRule -ErrorAction Stop", script);
        Assert.Contains("-Protocol TCP -LocalPort 18779", script);
        Assert.Contains("-Protocol UDP -LocalPort 18778", script);
        Assert.Contains("-RemoteAddress LocalSubnet", script);
        Assert.Contains("-Profile Any", script);
    }

    [Fact]
    public void WindowsServiceManager_FirewallScriptRemovesRulesWhenLanAccessIsDisabled()
    {
        var manager = new WindowsServiceManager();
        var config = new PluginConfig
        {
            BindAddress = "127.0.0.1",
            HttpPort = 18779,
            DiscoveryPort = 18778
        };

        var script = manager.BuildFirewallScript(config);

        Assert.Contains("Remove-NetFirewallRule -ErrorAction Stop", script);
        Assert.DoesNotContain("New-NetFirewallRule", script);
        Assert.DoesNotContain("-LocalPort", script);
    }

    [Fact]
    public void WindowsServiceManager_RestartScriptStartsExistingService()
    {
        var manager = new WindowsServiceManager();

        var script = manager.BuildRestartScript();

        Assert.Contains("Get-Service -Name $serviceName", script);
        Assert.Contains("Stop-Service -Name $serviceName -Force", script);
        Assert.Contains("Start-Service -Name $serviceName", script);
        Assert.DoesNotContain("sc.exe create", script);
        Assert.DoesNotContain("sc.exe delete", script);
    }

    [Fact]
    public void WindowsServiceManager_StartScriptIsIdempotent()
    {
        var manager = new WindowsServiceManager();

        var script = manager.BuildStartScript();

        Assert.Contains("Get-Service -Name $serviceName", script);
        Assert.Contains("if ($existing.Status -ne \"Running\")", script);
        Assert.Contains("Start-Service -Name $serviceName", script);
    }

    [Fact]
    public void WindowsServiceManager_CandidatePathsIncludePublishedAndDevelopmentService()
    {
        var candidates = WindowsServiceManager.GetCandidateServiceExePaths(
            "C:\\App\\tray\\");

        Assert.Contains("C:\\App\\service\\EntreePrintPlugin.exe", candidates);

        var developmentCandidates = WindowsServiceManager.GetCandidateServiceExePaths(
            "C:\\Repo\\csharp\\EntreePrintTray\\bin\\Debug\\net10.0-windows\\");

        Assert.Contains(
            "C:\\Repo\\csharp\\EntreePrintPlugin\\bin\\Debug\\net10.0-windows\\EntreePrintPlugin.exe",
            developmentCandidates);
    }

    [Fact]
    public void PluginConfig_DefaultsToWindowsServiceAutoInstall()
    {
        var config = new PluginConfig();

        Assert.True(config.AutoInstallWindowsService);
    }

    [Fact]
    public void PluginConfig_DoesNotAssumePrinterDeviceCommands()
    {
        var config = new PluginConfig();

        var json = System.Text.Json.JsonSerializer.Serialize(config);
        Assert.DoesNotContain("DefaultDrawerCommandHex", json);
        Assert.DoesNotContain("HtmlPrintMode", json);
    }

    [Fact]
    public void PluginConfig_WithPortChangesPreservesOtherSettings()
    {
        var config = new PluginConfig
        {
            AccessToken = "fixture-token",
            HttpPort = 9779,
            DiscoveryPort = 9778
        };

        var changed = config with
        {
            HttpPort = 17779,
            DiscoveryPort = 17778
        };

        Assert.Equal("fixture-token", changed.AccessToken);
        Assert.Equal(17779, changed.HttpPort);
        Assert.Equal(17778, changed.DiscoveryPort);
    }

    [Fact]
    public void SingleInstanceGuard_AllowsOnlyOneOwnerForMutexName()
    {
        var mutexName = $@"Local\EntreePrintPlugin.Tests.{Guid.NewGuid():N}";

        using var first = SingleInstanceGuard.TryAcquire(mutexName);
        var secondIsPrimary = true;
        var secondThread = new Thread(() =>
        {
            using var second = SingleInstanceGuard.TryAcquire(mutexName);
            secondIsPrimary = second.IsPrimary;
        });
        secondThread.Start();
        secondThread.Join();

        Assert.True(first.IsPrimary);
        Assert.False(secondIsPrimary);
    }
}
