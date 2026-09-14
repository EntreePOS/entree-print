using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using EntreePrint.Security;

namespace EntreePrintTray;

internal static class ServiceCommand
{
    internal static void ValidateAction(string action)
    {
        if (action is not ("install" or "start" or "stop" or "restart" or "uninstall"))
            throw new ArgumentException("Unknown Windows service action.", nameof(action));
    }

    internal static string ParseAction(string[] args)
    {
        if (args.Length != 2 || args[0] != "--service-action")
            throw new ArgumentException("Service management accepts one action only, with no paths or scripts.");
        ValidateAction(args[1]);
        return args[1];
    }

    internal static void RunElevated(string action)
    {
        ValidateAction(action);
        ProtectedStorage.AssertTrustedPath(Application.ExecutablePath);
        var start = new ProcessStartInfo(Application.ExecutablePath)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.SystemDirectory
        };
        start.ArgumentList.Add("--service-action");
        start.ArgumentList.Add(action);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Windows could not open service management.");
            // The helper bounds the actual operation. Allow the operator time to
            // approve UAC and read its error dialog before returning to the tray.
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new IOException("The service action failed. The administrator window shows the reason. Check service status before trying again.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new UnauthorizedAccessException("The service action was cancelled at Windows administrator approval.", error);
        }
        catch (Win32Exception error) { throw new IOException("Windows could not open service management.", error); }
    }

    internal static bool TryRunCommand(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] != "--service-action") return false;
        try
        {
            var action = ParseAction(args);
            if (!ProtectedStorage.IsAdministrator()) throw new UnauthorizedAccessException("Windows administrator approval is required.");
            ProtectedStorage.AssertTrustedPath(Application.ExecutablePath);
            // Resolve only this installed package. Environment overrides and
            // development fallback paths never supply privileged executables.
            var serviceExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "service", "EntreePrintPlugin.exe"));
            ProtectedStorage.AssertTrustedPath(serviceExe);
            PluginConfig? config = null;
            if (action is "install" or "start" or "restart")
            {
                ProtectedStorage.AssertTrustedPath(PluginConfig.DefaultPath);
                config = PluginConfig.Load(PluginConfig.DefaultPath);
            }
            var script = new WindowsServiceManager().BuildServiceScript(action, serviceExe, config);
            var result = RunPowerShell(script);
            if (result.ExitCode != 0)
                throw new IOException(string.IsNullOrWhiteSpace(result.Error)
                    ? $"Windows service management failed (exit code {result.ExitCode})." : result.Error);
        }
        catch (Exception error)
        {
            exitCode = 1;
            MessageBox.Show(error.Message, "Entree Print service", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return true;
    }

    internal static ProcessStartInfo PowerShellStartInfo(string script)
    {
        var system = Environment.SystemDirectory;
        var shellDirectory = Path.Combine(system, "WindowsPowerShell", "v1.0");
        var start = new ProcessStartInfo(Path.Combine(shellDirectory, "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = system,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["PSModulePath"] = Path.Combine(shellDirectory, "Modules");
        start.Environment["PATH"] = system;
        // Windows PowerShell adds its shared module directory during startup.
        // Reset discovery inside the shell before any command can auto-load.
        var command = "$env:PSModulePath = '" + Path.Combine(shellDirectory, "Modules").Replace("'", "''") + "'\n" +
            "[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" +
            "$ErrorActionPreference = 'Stop'\n$ProgressPreference = 'SilentlyContinue'\n" +
            "try {\n& {\n" + script + "\n}\nexit 0\n} catch {\n" +
            "[Console]::Error.WriteLine($_.Exception.Message)\nexit 1\n}";
        // Windows PowerShell requires UTF-16LE for EncodedCommand. This encoding
        // preserves literal Unicode; it is not a secrecy or authentication layer.
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
            start.ArgumentList.Add(argument);
        return start;
    }

    internal static (int ExitCode, string Output, string Error) RunPowerShell(string script, int timeoutMs = 90000)
    {
        using var process = Process.Start(PowerShellStartInfo(script))
            ?? throw new IOException("Windows could not launch service management.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Windows service management timed out. The service may have changed state; check its status before trying again.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult().Trim());
    }
}
