using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using EntreePrint.Security;

namespace EntreePrintTray;

internal static class SettingsWriter
{
    internal const int MaximumRequestBytes = 1_000_000;

    internal static async Task SaveAsync(PluginConfig config, string path)
    {
        var bytes = config.ToValidatedJson(); // Validate before asking Windows for elevation.
        if (bytes.Length > MaximumRequestBytes) throw new ArgumentException("The settings request exceeds 1 MB.");
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
                // The caller supplies data only, never a privileged output path or command.
                ReadRequest(args[1]).Save(PluginConfig.DefaultPath);
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

    internal static PluginConfig ReadRequest(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if ((File.GetAttributes(fullPath) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new ArgumentException("The settings request must be a regular file.");
        // Hold one handle without write/delete sharing through the complete read.
        // Never reopen the pathname or treat a missing request as default settings.
        using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if ((File.GetAttributes(file.SafeFileHandle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new ArgumentException("The settings request must be a regular file.");
        return ReadRequest(file);
    }

    internal static PluginConfig ReadRequest(Stream file)
    {
        if (file.Length > MaximumRequestBytes) throw new ArgumentException("The settings request exceeds 1 MB.");
        // Bound actual bytes as well as Length: never trust an earlier size observation.
        var bytes = new byte[MaximumRequestBytes + 1];
        var count = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > MaximumRequestBytes) throw new ArgumentException("The settings request exceeds 1 MB.");
        return PluginConfig.ParseRequest(bytes.AsSpan(0, count));
    }
}
