extern alias ServiceProject;
using System.Security.AccessControl;
using System.Security.Principal;
using EntreePrintTray;
using Storage = ServiceProject::EntreePrint.Security.ProtectedStorage;

namespace EntreePrintPlugin.Tests;

public sealed class ProtectedStorageTests
{
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    [Fact]
    public void ConfigurationAllowsLocalReading_AndPrivateReceiptDirectoryDoesNot()
    {
        Storage.ValidateDescriptor(Storage.ConfigurationPermissions());
        Storage.ValidateDescriptor(Storage.DirectoryPermissions(privateData: false));
        Storage.ValidateDescriptor(Storage.DirectoryPermissions(privateData: true), privateData: true);
        Assert.Throws<UnauthorizedAccessException>(() => Storage.ValidateDescriptor(Storage.DirectoryPermissions(false), privateData: true));
    }

    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.WriteAttributes)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    public void NonAdministrativeMutationIsRejected(FileSystemRights rights)
    {
        var security = Storage.DirectoryPermissions(false);
        security.AddAccessRule(new FileSystemAccessRule(Users, rights, AccessControlType.Allow));
        Assert.Throws<UnauthorizedAccessException>(() => Storage.ValidateDescriptor(security));
    }

    [Fact]
    public void UserOwnershipIsRejectedEvenWhenTheDaclLooksReadOnly()
    {
        var security = Storage.ConfigurationPermissions();
        security.SetOwner(new SecurityIdentifier("S-1-5-21-1-2-3-1001"));
        Assert.Throws<UnauthorizedAccessException>(() => Storage.ValidateDescriptor(security));
    }

    [Fact]
    public void NullDaclIsRejected()
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm("O:BAG:BAD:NO_ACCESS_CONTROL");
        Assert.Throws<UnauthorizedAccessException>(() => Storage.ValidateDescriptor(security));
    }

    [Fact]
    public void AncestorMayAllowCreatingOtherDirectories_ButNotReplacingProtectedChildren()
    {
        var security = Storage.DirectoryPermissions(false);
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.CreateDirectories, AccessControlType.Allow));
        Storage.ValidateDescriptor(security, ancestor: true);
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow));
        Assert.Throws<UnauthorizedAccessException>(() => Storage.ValidateDescriptor(security, ancestor: true));
    }

    [Fact]
    public void ExistingUserOwnedDataIsRejectedWithoutChangingItsAclOrContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EntreeProtectedDataTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var receipt = Path.Combine(directory, "receipt.json"); File.WriteAllText(receipt, "retained");
        var before = new DirectoryInfo(directory).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All & ~AccessControlSections.Audit);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Storage.EnsureDirectory(directory, privateData: true));
            Assert.Equal("retained", File.ReadAllText(receipt));
            Assert.Equal(before, new DirectoryInfo(directory).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All & ~AccessControlSections.Audit));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task TestConfigurationSaveRemainsIsolatedAndValidatesBeforeWriting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EntreeSettingsWriterTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            await SettingsWriter.SaveAsync(new PluginConfig { HttpPort = 19779 }, path);
            var before = File.ReadAllBytes(path);
            await Assert.ThrowsAsync<ArgumentException>(() => SettingsWriter.SaveAsync(new PluginConfig { HttpPort = -1 }, path));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
