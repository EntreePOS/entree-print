using System.Diagnostics;
using System.Text;
using EntreePrintTray;

namespace EntreePrintPlugin.Tests;

public sealed class ServiceSetupTests
{
    // Execute generated scripts in an ordinary child shell with every service/firewall
    // operation replaced. These tests never elevate or change Windows configuration.
    private const string Stubs = """
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $script:service = [pscustomobject]@{ Status = 'Stopped' }
        $script:service | Add-Member ScriptMethod WaitForStatus {
            param($status, $timeout)
            if ($script:waitFailure) { throw 'Windows service did not reach requested state' }
            Write-Output "wait:$status"
        }
        function Get-Service { param($Name, $ErrorAction) $script:service }
        function Stop-Service { param($Name, [switch]$Force, $ErrorAction) Write-Output 'stop' }
        function Start-Service { param($Name, $ErrorAction) Write-Output 'start' }
        function New-Service { param($Name, $BinaryPathName, $StartupType, $DisplayName, $ErrorAction)
            [Console]::WriteLine($BinaryPathName)
        }
        function sc.exe { $global:LASTEXITCODE = 0 }
        function Start-Sleep { param($Seconds) }
        function Get-NetFirewallRule { param($ErrorAction) }
        function Remove-NetFirewallRule { param($ErrorAction) }
        function New-NetFirewallRule { param($DisplayName, $Direction, $Action, $Protocol, $LocalPort, $RemoteAddress, $Profile, $Enabled, $ErrorAction) }
        """;

    [Fact]
    public async Task Install_PreservesLiteralExecutablePathAndWaitsForRunning()
    {
        const string path = "C:\\Program Files\\收据 '$($null = 1) ` test\\EntreePrintPlugin.exe";
        var result = await Execute(new WindowsServiceManager().BuildInstallScript(path, new PluginConfig()));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains('"' + path + '"', result.Output);
        Assert.Contains("wait:Running", result.Output);
        Assert.Equal("", result.Error);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("restart")]
    public async Task MissingService_ReportsFailure(string action)
    {
        var manager = new WindowsServiceManager();
        var script = action == "start" ? manager.BuildStartScript() : manager.BuildRestartScript();
        var result = await Execute(script, "$script:service = $null");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("not installed", result.Error);
        Assert.DoesNotContain("start", result.Output);
    }

    [Fact]
    public async Task StartupError_IsReturnedInsteadOfSuccess()
    {
        var result = await Execute(new WindowsServiceManager().BuildStartScript(),
            "function Start-Service { throw 'Windows rejected service startup' }");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Windows rejected service startup", result.Error);
    }

    [Fact]
    public async Task StateTimeout_IsReturnedInsteadOfSuccess()
    {
        var result = await Execute(new WindowsServiceManager().BuildRestartScript(), "$script:waitFailure = $true");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("did not reach requested state", result.Error);
    }

    [Fact]
    public async Task FailedNativeConfiguration_DoesNotContinueToStart()
    {
        var result = await Execute(new WindowsServiceManager().BuildInstallScript(@"C:\fixture.exe", new PluginConfig()),
            "$script:service = $null\nfunction sc.exe { $global:LASTEXITCODE = 5 }");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Windows error 5", result.Error);
        Assert.DoesNotContain("start", result.Output);
    }

    [Fact]
    public async Task FirewallError_StopsSetupAndReturnsReason()
    {
        var result = await Execute(new WindowsServiceManager().BuildStartScript(new PluginConfig()),
            "function Get-NetFirewallRule { throw 'Firewall access denied' }");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Firewall access denied", result.Error);
        Assert.DoesNotContain("start", result.Output);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("127.0.0.2")]
    [InlineData("localhost")]
    public async Task LoopbackOnly_DoesNotOpenLanPorts(string bindAddress)
    {
        var result = await Execute(new WindowsServiceManager().BuildFirewallScript(new PluginConfig { BindAddress = bindAddress }),
            "function New-NetFirewallRule { throw 'Must not open LAN ports' }");
        Assert.Equal(0, result.ExitCode);
    }

    private static async Task<(int ExitCode, string Output, string Error)> Execute(string script, string overrides = "")
    {
        var directory = Path.Combine(Path.GetTempPath(), "EntreeServiceSetupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // Quotes and dollar expressions must remain literal even in the diagnostic path.
        var errorPath = Path.Combine(directory, "收据 '$(throw).txt");
        var scriptPath = Path.Combine(directory, "fixture.ps1");
        try
        {
            await File.WriteAllTextAsync(scriptPath,
                WindowsServiceManager.BuildCheckedScript(Stubs + "\n" + overrides + "\n" + script, errorPath), Encoding.UTF8);
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath })
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            Assert.Equal("", await stderr);
            return (process.ExitCode, await stdout, File.Exists(errorPath) ? await File.ReadAllTextAsync(errorPath) : "");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
