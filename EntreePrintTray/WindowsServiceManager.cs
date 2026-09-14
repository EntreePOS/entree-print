using System.Diagnostics;
using System.Text;

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

    public void Install(PluginConfig config)
    {
        var exePath = ResolveServiceExe();
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException("Service executable was not found. Publish the service first or set ENTREE_PRINT_SERVICE_EXE.", exePath);
        }

        RunElevatedScript(BuildInstallScript(exePath, config));
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
            "sc.exe description $serviceName \"Local LAN HTML print plugin for ENTREE POS tablet clients.\" | Out-Null",
            "if ($LASTEXITCODE -ne 0) { throw \"Could not configure service (Windows error $LASTEXITCODE).\" }",
            BuildFirewallScript(config),
            "Start-Service -Name $serviceName -ErrorAction Stop",
            "(Get-Service -Name $serviceName -ErrorAction Stop).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))");
    }

    public void Uninstall()
    {
        RunElevatedScript(Lines(
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
            "}"));
    }

    public void Start(PluginConfig? config = null)
    {
        RunElevatedScript(BuildStartScript(config));
    }

    public void Stop()
    {
        RunElevatedScript(Lines(
            $"Stop-Service -Name '{ServiceName}' -Force -ErrorAction Stop",
            $"(Get-Service -Name '{ServiceName}' -ErrorAction Stop).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))"));
    }

    public void Restart(PluginConfig? config = null)
    {
        RunElevatedScript(BuildRestartScript(config));
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
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -Command \"try {{ Get-Service -ErrorAction Stop | Where-Object Name -eq '{ServiceName}' | ForEach-Object {{ Write-Output $_.Status }}; exit 0 }} catch {{ exit 2 }}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (process is null)
        {
            throw new InvalidOperationException("Could not launch the Windows service status check.");
        }

        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Windows did not respond when checking the print service.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Windows could not check the print service status.");
        var status = output.GetAwaiter().GetResult().Trim();
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

    private static void RunElevatedScript(string script)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"entree-print-service-{Guid.NewGuid():N}.ps1");
        var errorPath = Path.ChangeExtension(scriptPath, ".error.txt");
        try
        {
            File.WriteAllText(scriptPath, BuildCheckedScript(script, errorPath), Encoding.UTF8);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("Could not launch Windows service setup.");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                var detail = File.Exists(errorPath) ? File.ReadAllText(errorPath).Trim() : "";
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? $"Windows service setup failed (exit code {process.ExitCode})."
                    : detail);
            }
        }
        finally
        {
            foreach (var path in new[] { scriptPath, errorPath })
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static string BuildCheckedScript(string script, string errorPath) => Lines(
        "$ErrorActionPreference = 'Stop'",
        "try {",
        script,
        "  exit 0",
        "} catch {",
        $"  [IO.File]::WriteAllText('{EscapePowerShellSingleQuoted(errorPath)}', $_.Exception.Message, [Text.Encoding]::UTF8)",
        "  exit 1",
        "}");

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
