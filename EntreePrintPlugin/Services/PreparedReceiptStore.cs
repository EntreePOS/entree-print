using System.Security.Cryptography;
using System.Text.Json;
using EntreePrintPlugin.Models;

namespace EntreePrintPlugin.Services;

public sealed class PreparedReceiptStore
{
    private readonly string _directory;
    private readonly ServiceIdentity _identity;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    public PreparedReceiptStore(string directory, ServiceIdentity identity, TimeProvider? clock = null)
    {
        _directory = Path.GetFullPath(directory); _identity = identity; _clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(_directory);
    }

    public PreparedReceipt Save(string printer, decimal widthMm, string profileVersion, ReceiptTextLayout layout, int? dpi = null)
    {
        WindowsTextPrinter.Validate(layout);
        lock (_gate)
        {
            foreach (var expiredPath in Directory.GetFiles(_directory, "*.json")
                .Where(path => File.GetLastWriteTimeUtc(path) < _clock.GetUtcNow().UtcDateTime.AddMinutes(-30)))
            {
                var expired = Get(Path.GetFileNameWithoutExtension(expiredPath), allowExpired: true);
                if (expired.ExpiresAt <= _clock.GetUtcNow()) File.Delete(expiredPath);
            }
            var files = Directory.GetFiles(_directory, "*.json");
            if (files.Length >= 1000 || files.Sum(path => new FileInfo(path).Length) >= 256_000_000)
                throw new CommandException("RENDER_STORE_FULL", "Prepared receipt storage is full; expire unused previews before preparing more.");
            var now = _clock.GetUtcNow();
            var receipt = new PreparedReceipt(Guid.NewGuid().ToString("D"), _identity.ServiceId, printer, widthMm,
                profileVersion, Hash(printer, widthMm, profileVersion, layout, dpi), layout, now, now.AddMinutes(30), dpi);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt);
            if (bytes.Length > 8_000_000) throw new CommandException("RENDER_TOO_LARGE", "Prepared receipt exceeds 8 MB.");
            var path = RecordPath(receipt.Id);
            var temporary = path + ".tmp";
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            File.Move(temporary, path);
            return receipt;
        }
    }

    public PreparedReceipt Get(string id, bool allowExpired = false)
    {
        lock (_gate)
        {
            var path = RecordPath(id);
            if (!File.Exists(path)) throw new CommandException("RENDER_NOT_FOUND", "Prepared receipt was not found.");
            if (new FileInfo(path).Length > 8_000_000) throw new InvalidDataException("Prepared receipt exceeds storage bounds.");
            var receipt = JsonSerializer.Deserialize<PreparedReceipt>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Invalid prepared receipt.");
            if (receipt.Id != id || receipt.ServiceId != _identity.ServiceId
                || receipt.ContentHash != Hash(receipt.Printer, receipt.WidthMm, receipt.ProfileVersion, receipt.Layout, receipt.Dpi))
                throw new InvalidDataException("Prepared receipt integrity check failed.");
            if (!allowExpired && receipt.ExpiresAt <= _clock.GetUtcNow()) throw new CommandException("RENDER_EXPIRED", "Preview expired; prepare and review it again before printing.");
            return receipt;
        }
    }

    public static string Hash(string printer, decimal widthMm, string profile, ReceiptTextLayout layout, int? dpi = null) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { printer, widthMm, profile, layout, dpi }))).ToLowerInvariant();

    public static object View(PreparedReceipt receipt) => new
    {
        receipt.Id, receipt.ServiceId,
        html = "<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><style>html,body{margin:0;padding:0}svg{display:block}</style></head><body>"
            + WindowsTextPrinter.ToSvg(receipt.Layout) + "</body></html>",
        receipt.WidthMm, heightMm = (decimal)receipt.Layout.Height * 25.4m / 96m,
        renderer = "positioned-text", rendererVersion = ServiceIdentity.Version,
        receipt.ProfileVersion, receipt.Dpi, settingsSource = "windows_driver", receipt.ExpiresAt, warnings = Array.Empty<string>()
    };

    private string RecordPath(string id)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed) || parsed.ToString("D") != id)
            throw new CommandException("RENDER_NOT_FOUND", "Invalid prepared receipt ID.");
        return Path.Combine(_directory, id + ".json");
    }
}
