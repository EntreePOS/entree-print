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
        function Get-CimInstance { param($ClassName, $Filter, $ErrorAction)
            if ($script:service) { [pscustomobject]@{ PathName = $expectedCommand } }
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
        var result = await Execute(new WindowsServiceManager().BuildServiceScript("install", path, new PluginConfig()));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains('"' + path + '"', result.Output);
        Assert.Contains("wait:Running", result.Output);
        Assert.Equal("", result.Error);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("restart")]
    [InlineData("stop")]
    public async Task MissingService_ReportsFailure(string action)
    {
        var manager = new WindowsServiceManager();
        var script = manager.BuildServiceScript(action, @"C:\fixture.exe", new PluginConfig());
        var result = await Execute(script, "$script:service = $null");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("not installed", result.Error);
        Assert.DoesNotContain("start", result.Output);
    }

    [Fact]
    public async Task StartupError_IsReturnedInsteadOfSuccess()
    {
        var result = await Execute(new WindowsServiceManager().BuildServiceScript("start", @"C:\fixture.exe", new PluginConfig()),
            "function Start-Service { throw 'Windows rejected service startup' }");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Windows rejected service startup", result.Error);
    }

    [Fact]
    public async Task StateTimeout_IsReturnedInsteadOfSuccess()
    {
        var result = await Execute(new WindowsServiceManager().BuildServiceScript("restart", @"C:\fixture.exe", new PluginConfig()), "$script:waitFailure = $true");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("did not reach requested state", result.Error);
    }

    [Fact]
    public async Task FailedNativeConfiguration_DoesNotContinueToStart()
    {
        var result = await Execute(new WindowsServiceManager().BuildServiceScript("install", @"C:\fixture.exe", new PluginConfig()),
            "$script:service = $null\nfunction sc.exe { $global:LASTEXITCODE = 5 }");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Windows error 5", result.Error);
        Assert.DoesNotContain("start", result.Output);
    }

    [Fact]
    public async Task FirewallError_StopsSetupAndReturnsReason()
    {
        var result = await Execute(new WindowsServiceManager().BuildServiceScript("start", @"C:\fixture.exe", new PluginConfig()),
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

    [Theory]
    [InlineData("install")]
    [InlineData("start")]
    [InlineData("restart")]
    [InlineData("stop")]
    [InlineData("uninstall")]
    public async Task UnexpectedServiceCommand_RejectsBeforeAnyMutation(string action)
    {
        foreach (var registered in new[] { @"C:\foreign.exe", @"C:\fixture.exe", "\"C:\\fixture.exe\" --other-config" })
        {
            var script = new WindowsServiceManager().BuildServiceScript(action, @"C:\fixture.exe", new PluginConfig());
            var result = await Execute(script,
                "function Get-CimInstance { [pscustomobject]@{ PathName = '" + registered + "' } }\n" +
                "function Get-Service { throw 'Service was touched' }\n" +
                "function Get-NetFirewallRule { throw 'Firewall was touched' }");
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("another installation", result.Error);
            Assert.Equal("", result.Output);
        }
    }

    [Fact]
    public async Task MissingService_UninstallDoesNothing()
    {
        var result = await Execute(new WindowsServiceManager().BuildServiceScript("uninstall", @"C:\fixture.exe", null),
            "function Get-CimInstance { }\nfunction Get-Service { throw 'Must not touch services' }");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Equal("", result.Error);
    }

    [Theory]
    [InlineData("install", "wait:Running")]
    [InlineData("start", "wait:Running")]
    [InlineData("restart", "wait:Running")]
    [InlineData("stop", "wait:Stopped")]
    [InlineData("uninstall", "deleted")]
    public async Task OwnedService_AllActionsSucceed(string action, string expected)
    {
        var result = await Execute(new WindowsServiceManager().BuildServiceScript(action, @"C:\fixture.exe", new PluginConfig()),
            "function sc.exe { $global:LASTEXITCODE = 0; if ($args[0] -eq 'delete') { [Console]::WriteLine('deleted') } }");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Contains(expected, result.Output);
    }

    [Fact]
    public async Task FailedOwnershipLookup_DoesNotTouchServiceOrFirewall()
    {
        var result = await Execute(new WindowsServiceManager().BuildServiceScript("install", @"C:\fixture.exe", new PluginConfig()),
            "function Get-CimInstance { throw 'Windows inventory unavailable' }\n" +
            "function Get-Service { throw 'Must not touch service' }\n" +
            "function Get-NetFirewallRule { throw 'Must not touch firewall' }");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Windows inventory unavailable", result.Error);
        Assert.Equal("", result.Output);
    }

    [Fact]
    public void ServiceCommand_AcceptsOnlyFixedActions()
    {
        Assert.Equal("start", ServiceCommand.ParseAction(["--service-action", "start"]));
        Assert.Throws<ArgumentException>(() => ServiceCommand.ParseAction(["--service-action", "start", @"C:\request.ps1"]));
        Assert.Throws<ArgumentException>(() => ServiceCommand.ParseAction(["--service-action", "start; Write-Output injected"]));
        Assert.Throws<ArgumentException>(() => ServiceCommand.ParseAction(["--service-action"]));
    }

    [Fact]
    public async Task Shell_UsesOnlySystemCommandAndModuleLocations()
    {
        var start = ServiceCommand.PowerShellStartInfo("Write-Output 'fixture'");
        var system = Environment.SystemDirectory;
        Assert.Equal(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"), start.FileName);
        Assert.Equal(system, start.WorkingDirectory);
        Assert.Contains("-NoProfile", start.ArgumentList);
        Assert.Contains("-NonInteractive", start.ArgumentList);
        Assert.DoesNotContain("-File", start.ArgumentList);
        var result = await Execute("[Console]::WriteLine($env:PATH)\n[Console]::WriteLine($env:PSModulePath)");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { system, Path.Combine(system, "WindowsPowerShell", "v1.0", "Modules") },
            result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Shell_TimeoutReportsUncertainState()
    {
        var error = Assert.Throws<TimeoutException>(() =>
            ServiceCommand.RunPowerShell("[Threading.Thread]::Sleep(10000)", 1000));
        Assert.Contains("may have changed state", error.Message);
    }

    [Fact]
    public void Shell_ResolvesRequiredWindowsToolsWithoutExecutingThem()
    {
        var names = new[] { "Get-CimInstance", "Get-Service", "Stop-Service", "Start-Service", "New-Service",
            "Get-NetFirewallRule", "Remove-NetFirewallRule", "New-NetFirewallRule", "Start-Sleep" };
        var result = ServiceCommand.RunPowerShell(
            "Get-Command " + string.Join(",", names) + " -ErrorAction Stop | ForEach-Object { [Console]::WriteLine($_.Name) }\n" +
            "[Console]::WriteLine((Get-Command sc.exe -CommandType Application -ErrorAction Stop).Source)", 20000);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        foreach (var name in names) Assert.Contains(name, result.Output);
        Assert.Contains(Path.Combine(Environment.SystemDirectory, "sc.exe"), result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<(int ExitCode, string Output, string Error)> Execute(string script, string overrides = "") =>
        Task.Run(() => ServiceCommand.RunPowerShell(Stubs + "\n" + overrides + "\n" + script));
}
