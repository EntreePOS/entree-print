namespace EntreePrintTray;

public sealed class WindowsServiceManager
{
    private const string ServiceName = "EntreePrintPlugin";
    private const string DisplayName = "ENTREE Print Plugin";
    private const string HttpFirewallRuleName = "ENTREE Print Plugin HTTP";
    private const string LegacyTcpSdkFirewallRuleName = "ENTREE Print Plugin TCP SDK";
    private const string DiscoveryFirewallRuleName = "ENTREE Print Plugin Discovery";

    public string ResolveServiceExe()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("ENTREE_PRINT_SERVICE_EXE");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var baseDirectory = AppContext.BaseDirectory;
        foreach (var candidate in GetCandidateServiceExePaths(baseDirectory))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return GetCandidateServiceExePaths(baseDirectory).First();
    }

    public static IReadOnlyList<string> GetCandidateServiceExePaths(string baseDirectory)
    {
        return
        [
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "service", "EntreePrintPlugin.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "EntreePrintPlugin.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "EntreePrintPlugin", "bin", "Debug", "net10.0-windows", "EntreePrintPlugin.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "EntreePrintPlugin", "bin", "Release", "net10.0-windows", "EntreePrintPlugin.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "EntreePrintPlugin", "bin", "Release", "net10.0-windows", "win-x64", "EntreePrintPlugin.exe"))
        ];
    }

    public void Install() => ServiceCommand.RunElevated("install");
    public void Uninstall() => ServiceCommand.RunElevated("uninstall");
    public void Start() => ServiceCommand.RunElevated("start");
    public void Stop() => ServiceCommand.RunElevated("stop");
    public void Restart() => ServiceCommand.RunElevated("restart");

    internal string BuildServiceScript(string action, string exePath, PluginConfig? config)
    {
        ServiceCommand.ValidateAction(action);
        var operation = action switch
        {
            "install" => BuildInstallScript(exePath, config ?? throw new ArgumentNullException(nameof(config))),
            "start" => BuildStartScript(config),
            "restart" => BuildRestartScript(config),
            "stop" => Lines("Stop-Service -Name $serviceName -Force -ErrorAction Stop",
                "(Get-Service -Name $serviceName -ErrorAction Stop).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))"),
            _ => BuildUninstallScript()
        };
        // Check the SCM command before any service or firewall mutation. An extra
        // argument or an unquoted/foreign executable is not this installation.
        return Lines(
            $"$serviceName = '{ServiceName}'",
            $"$expectedCommand = '\"{EscapePowerShellSingleQuoted(exePath)}\"'",
            "$registered = Get-CimInstance -ClassName Win32_Service -Filter \"Name='$serviceName'\" -ErrorAction Stop",
            "if ($registered -and -not [string]::Equals(([string]$registered.PathName).Trim(), $expectedCommand, [StringComparison]::OrdinalIgnoreCase)) { throw 'The print service belongs to another installation or has an unexpected command. No changes were made.' }",
            action == "install" ? "" : action == "uninstall"
                ? "if (-not $registered) { return }"
                : "if (-not $registered) { throw \"Service $serviceName is not installed.\" }",
            operation);
    }

    public string BuildInstallScript(string exePath, PluginConfig config)
    {
        return Lines(
            "$ErrorActionPreference = \"Stop\"",
            $"$serviceName = \"{EscapePowerShell(ServiceName)}\"",
            $"$displayName = \"{EscapePowerShell(DisplayName)}\"",
            $"$exePath = '{EscapePowerShellSingleQuoted(exePath)}'",
            "$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue",
            "if ($existing) {",
            "  if ($existing.Status -ne \"Stopped\") {",
            "    Stop-Service -Name $serviceName -Force -ErrorAction Stop",
            "    $existing.WaitForStatus(\"Stopped\", [TimeSpan]::FromSeconds(20))",
            "  }",
            "  sc.exe delete $serviceName | Out-Null",
            "  if ($LASTEXITCODE -ne 0) { throw \"Could not remove service (Windows error $LASTEXITCODE).\" }",
            "  Start-Sleep -Seconds 2",
            "}",
            "New-Service -Name $serviceName -BinaryPathName ('\"' + $exePath + '\"') -StartupType Automatic -DisplayName $displayName -ErrorAction Stop | Out-Null",
            "sc.exe description $serviceName \"Entree Print: Windows printing for local applications.\" | Out-Null",
            "if ($LASTEXITCODE -ne 0) { throw \"Could not configure service (Windows error $LASTEXITCODE).\" }",
            BuildFirewallScript(config),
            "Start-Service -Name $serviceName -ErrorAction Stop",
            "(Get-Service -Name $serviceName -ErrorAction Stop).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))");
    }

    private static string BuildUninstallScript()
    {
        return Lines(
            "$ErrorActionPreference = \"Stop\"",
            $"$serviceName = \"{EscapePowerShell(ServiceName)}\"",
            "$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue",
            "if ($existing) {",
            "  if ($existing.Status -ne \"Stopped\") {",
            "    Stop-Service -Name $serviceName -Force -ErrorAction Stop",
            "    $existing.WaitForStatus(\"Stopped\", [TimeSpan]::FromSeconds(20))",
            "  }",
            "  sc.exe delete $serviceName | Out-Null",
            "  if ($LASTEXITCODE -ne 0) { throw \"Could not remove service (Windows error $LASTEXITCODE).\" }",
            "}");
    }

    public bool IsInstalled()
    {
        return QueryServiceStatus() is not null;
    }

    public bool IsRunning()
    {
        return string.Equals(QueryServiceStatus(), "Running", StringComparison.OrdinalIgnoreCase);
    }

    private static string? QueryServiceStatus()
    {
        var result = ServiceCommand.RunPowerShell(
            $"Get-Service -ErrorAction Stop | Where-Object Name -eq '{ServiceName}' | ForEach-Object {{ Write-Output $_.Status }}", 5000);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Windows could not check the print service status.");
        var status = result.Output.Trim();
        return string.IsNullOrEmpty(status) ? null : status;
    }

    public string BuildRestartScript(PluginConfig? config = null)
    {
        return Lines(
            "$ErrorActionPreference = 'Stop'",
            $"$serviceName = \"{EscapePowerShell(ServiceName)}\"",
            config is null ? "" : BuildFirewallScript(config),
            "$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue",
            "if (-not $existing) { throw \"Service $serviceName is not installed.\" }",
            "if ($existing) {",
            "  if ($existing.Status -ne \"Stopped\") {",
            "    Stop-Service -Name $serviceName -Force -ErrorAction Stop",
            "    $existing.WaitForStatus(\"Stopped\", [TimeSpan]::FromSeconds(20))",
            "  }",
            "  Start-Service -Name $serviceName -ErrorAction Stop",
            "  $existing.WaitForStatus('Running', [TimeSpan]::FromSeconds(20))",
            "}");
    }

    public string BuildStartScript(PluginConfig? config = null)
    {
        return Lines(
            "$ErrorActionPreference = 'Stop'",
            $"$serviceName = \"{EscapePowerShell(ServiceName)}\"",
            config is null ? "" : BuildFirewallScript(config),
            "$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue",
            "if (-not $existing) { throw \"Service $serviceName is not installed.\" }",
            "if ($existing.Status -ne \"Running\") {",
            "  Start-Service -Name $serviceName -ErrorAction Stop",
            "  $existing.WaitForStatus('Running', [TimeSpan]::FromSeconds(20))",
            "}");
    }

    public string BuildFirewallScript(PluginConfig config)
    {
        var lines = new List<string>
        {
            $"$ruleNames = @('{HttpFirewallRuleName}', '{LegacyTcpSdkFirewallRuleName}', '{DiscoveryFirewallRuleName}')",
            "Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.DisplayName -in $ruleNames } | Remove-NetFirewallRule -ErrorAction Stop"
        };

        if (IsLocalNetworkAccessible(config))
        {
            lines.Add($"New-NetFirewallRule -DisplayName '{HttpFirewallRuleName}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {config.HttpPort} -RemoteAddress LocalSubnet -Profile Any -Enabled True -ErrorAction Stop | Out-Null");
            lines.Add($"New-NetFirewallRule -DisplayName '{DiscoveryFirewallRuleName}' -Direction Inbound -Action Allow -Protocol UDP -LocalPort {config.DiscoveryPort} -RemoteAddress LocalSubnet -Profile Any -Enabled True -ErrorAction Stop | Out-Null");
        }

        return Lines([.. lines]);
    }

    private static bool IsLocalNetworkAccessible(PluginConfig config)
    {
        return config.IsLanAccessible;
    }

    private static string Lines(params string[] lines)
    {
        return string.Join(Environment.NewLine, lines);
    }

    private static string EscapePowerShell(string value)
    {
        return value.Replace("`", "``", StringComparison.Ordinal)
            .Replace("\"", "`\"", StringComparison.Ordinal);
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}
