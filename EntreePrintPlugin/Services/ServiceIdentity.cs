using System.Text.Json;

namespace EntreePrintPlugin.Services;

public sealed class ServiceIdentity
{
    public const string Version = "0.0.1-beta";
    public string ServiceId { get; }
    public string BootId { get; } = Guid.NewGuid().ToString("D");

    public ServiceIdentity(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "service-identity.json");
        if (!File.Exists(path))
        {
            var temporary = Path.Combine(directory, $".identity-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, new IdentityRecord(1, Guid.NewGuid().ToString("D")));
                    file.Flush(flushToDisk: true);
                }
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { } // Another initializer won; read its complete identity.
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var stored = JsonSerializer.Deserialize<IdentityRecord>(File.ReadAllBytes(path));
        if (stored is null || stored.Schema != 1 || !Guid.TryParseExact(stored.ServiceId, "D", out var id) || id == Guid.Empty)
            throw new InvalidDataException("Stored service identity is invalid. Restore the identity with its job ledger; do not silently create a different server.");
        ServiceId = stored.ServiceId;
    }

    private sealed record IdentityRecord(int Schema, string ServiceId);
}
