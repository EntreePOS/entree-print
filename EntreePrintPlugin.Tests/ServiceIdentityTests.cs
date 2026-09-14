using EntreePrintPlugin.Services;

namespace EntreePrintPlugin.Tests;

public sealed class ServiceIdentityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EntreeIdentityTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Restart_KeepsServerIdentityAndChangesBootIdentity()
    {
        var first = new ServiceIdentity(_directory);
        var restarted = new ServiceIdentity(_directory);
        Assert.Equal(first.ServiceId, restarted.ServiceId);
        Assert.NotEqual(first.BootId, restarted.BootId);
    }

    [Fact]
    public async Task ConcurrentInitialization_UsesOneDurableIdentity()
    {
        var identities = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => new ServiceIdentity(_directory))));
        Assert.Single(identities.Select(identity => identity.ServiceId).Distinct());
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void InvalidIdentity_IsNotSilentlyReplaced()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "service-identity.json");
        const string invalid = "{\"Schema\":1,\"ServiceId\":\"not-a-service-id\"}";
        File.WriteAllText(path, invalid);
        Assert.Throws<InvalidDataException>(() => new ServiceIdentity(_directory));
        Assert.Equal(invalid, File.ReadAllText(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
