using System.Security.AccessControl;
using System.Security.Principal;

namespace EntreePrint.Security;

internal static class ProtectedStorage
{
    internal static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    internal static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const FileSystemRights Mutable = FileSystemRights.Write | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    private const FileSystemRights ReplaceChild = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static bool IsTrustedIdentity(IdentityReference? identity) => identity is SecurityIdentifier sid &&
        (sid.Equals(Administrators) || sid.Equals(System) || sid.Value == TrustedInstaller);

    internal static void ValidateDescriptor(FileSystemSecurity security, bool privateData = false, bool ancestor = false)
    {
        if (!IsTrustedIdentity(security.GetOwner(typeof(SecurityIdentifier))))
            throw new UnauthorizedAccessException("The file or folder must be owned by Administrators, SYSTEM or TrustedInstaller.");
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (raw.DiscretionaryAcl is null) throw new UnauthorizedAccessException("An unrestricted access list is not allowed.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || IsTrustedIdentity(rule.IdentityReference) ||
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            var forbidden = ancestor ? ReplaceChild : privateData ? (FileSystemRights)(-1) : Mutable;
            if ((rule.FileSystemRights & forbidden) != 0)
                throw new UnauthorizedAccessException(privateData ? "Receipt storage must be accessible only to administrators and the service." :
                    "A non-administrator can change this file or folder. Use an administrator-protected installation.");
        }
    }

    internal static void AssertTrustedPath(string path, bool privateData = false)
    {
        var fullPath = Path.GetFullPath(path);
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Linked files and folders are not allowed for service storage or executables.");
        var directory = (attributes & FileAttributes.Directory) != 0;
        ValidateDescriptor(directory ? new DirectoryInfo(fullPath).GetAccessControl() : new FileInfo(fullPath).GetAccessControl(), privateData);
        var parent = directory ? Directory.GetParent(fullPath) : new FileInfo(fullPath).Directory;
        var immediate = !directory;
        while (parent is not null)
        {
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("A parent directory is a link.");
            ValidateDescriptor(parent.GetAccessControl(), ancestor: !immediate);
            immediate = false; parent = parent.Parent;
        }
    }

    internal static DirectorySecurity DirectoryPermissions(bool privateData)
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators); security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Administrators, System }) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (!privateData) security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    internal static FileSecurity ConfigurationPermissions()
    {
        var security = new FileSecurity();
        security.SetOwner(Administrators); security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Administrators, System }) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Read, AccessControlType.Allow));
        return security;
    }

    internal static void EnsureDirectory(string path, bool privateData)
    {
        if (!Directory.Exists(path))
        {
            var parent = Directory.GetParent(Path.GetFullPath(path)) ?? throw new ArgumentException("A storage parent is required.");
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("A storage parent is a link.");
            ValidateDescriptor(parent.GetAccessControl(), ancestor: true);
            new DirectoryInfo(path).Create(DirectoryPermissions(privateData));
        }
        // Never replace the ACL/owner of an existing directory or adopt its contents.
        AssertTrustedPath(path, privateData);
    }

    internal static void ValidatePrivateTree(string path)
    {
        AssertTrustedPath(path, privateData: true);
        var directories = new Stack<string>(); directories.Push(path);
        while (directories.TryPop(out var directory))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Linked receipt data is not allowed.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    ValidateDescriptor(new DirectoryInfo(child).GetAccessControl(), privateData: true); directories.Push(child);
                }
                else ValidateDescriptor(new FileInfo(child).GetAccessControl(), privateData: true);
            }
        }
    }
}
