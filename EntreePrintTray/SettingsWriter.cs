using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using EntreePrint.Security;

namespace EntreePrintTray;

internal static class SettingsWriter
{
    internal static async Task SaveAsync(PluginConfig config, string path)
    {
        var bytes = config.ToValidatedJson(); // Validate before asking Windows for elevation.
        if (!string.Equals(Path.GetFullPath(path), PluginConfig.DefaultPath, StringComparison.OrdinalIgnoreCase) || ProtectedStorage.IsAdministrator())
        {
            config.Save(path); return;
        }
        ProtectedStorage.AssertTrustedPath(Application.ExecutablePath);
        var request = Path.Combine(Path.GetTempPath(), "entree-settings-" + Guid.NewGuid().ToString("N") + ".json");
        using var identity = WindowsIdentity.GetCurrent();
        var permissions = new FileSecurity();
        permissions.SetOwner(identity.User!); permissions.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { identity.User!, ProtectedStorage.Administrators, ProtectedStorage.System })
            permissions.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        try
        {
            using (var stream = new FileInfo(request).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, permissions))
            {
                await stream.WriteAsync(bytes); stream.Flush(flushToDisk: true);
            }
            var start = new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--save-settings"); start.ArgumentList.Add(request);
            using var process = Process.Start(start) ?? throw new IOException("Windows could not open settings approval.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException("Service settings were not saved. The administrator window shows the reason.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new UnauthorizedAccessException("Settings were not saved because Windows administrator approval was cancelled.", error);
        }
        catch (Win32Exception error) { throw new IOException("Windows could not open settings approval.", error); }
        finally
        {
            try { File.Delete(request); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* The request remains protected by its explicit ACL. */ }
        }
    }

    internal static bool TryRunCommand(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || (args[0] != "--save-settings" && args[0] != "--initialize-storage")) return false;
        try
        {
            if (!ProtectedStorage.IsAdministrator()) throw new UnauthorizedAccessException("Windows administrator approval is required.");
            if (args[0] == "--initialize-storage")
            {
                if (args.Length != 1) throw new ArgumentException("Storage initialization does not accept paths or other options.");
                PluginConfig.InitializeInstalledStorage();
            }
            else
            {
                if (args.Length != 2) throw new ArgumentException("A settings request is required.");
                var request = new FileInfo(Path.GetFullPath(args[1]));
                if ((request.Attributes & FileAttributes.ReparsePoint) != 0 || request.Length > 1_000_000)
                    throw new ArgumentException("The settings request must be a regular file under 1 MB.");
                // The caller supplies data only, never a privileged output path or command.
                PluginConfig.Load(request.FullName).Save(PluginConfig.DefaultPath);
            }
        }
        catch (Exception error)
        {
            exitCode = 1;
            if (args[0] == "--save-settings")
                MessageBox.Show(error.Message + "\n\nExisting settings and receipt data were not adopted or reset.", "Entree Print settings",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return true;
    }
}
