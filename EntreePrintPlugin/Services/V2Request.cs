using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EntreePrintPlugin.Services;

public sealed record V2Request(JsonElement Body, string Digest)
{
    public const string DigestHeader = "X-Entree-Content-SHA256";
    public const int MaxBytes = 1_000_000;

    public static async Task<V2Request> ReadAsync(HttpRequest request, CancellationToken token)
    {
        if (request.ContentLength > MaxBytes) throw new CommandException("REQUEST_TOO_LARGE", "Request exceeds 1 MB.");
        if (!string.IsNullOrEmpty(request.Headers.ContentEncoding))
            throw new CommandException("CONTENT_ENCODING_UNSUPPORTED", "Send the uncompressed UTF-8 JSON bytes used to compute the digest.");
        if (!request.HasJsonContentType()) throw new CommandException("CONTENT_TYPE_INVALID", "Send application/json.");
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await request.Body.ReadAsync(buffer, token);
            if (count == 0) break;
            if (bytes.Length + count > MaxBytes) throw new CommandException("REQUEST_TOO_LARGE", "Request exceeds 1 MB.");
            bytes.Write(buffer, 0, count);
        }
        if (request.ContentLength is long declared && declared != bytes.Length)
            throw new CommandException("REQUEST_INCOMPLETE", "The uploaded byte count does not match Content-Length.");
        return Parse(bytes.ToArray(), request.Headers[DigestHeader].ToString());
    }

    public static V2Request Parse(byte[] bytes, string digest)
    {
        if (bytes.Length > MaxBytes) throw new CommandException("REQUEST_TOO_LARGE", "Request exceeds 1 MB.");
        if (string.IsNullOrEmpty(digest)) throw new CommandException("CHECKSUM_MISSING", "Request digest is required.");
        if (digest.Length != 64 || digest.Any(c => !char.IsAsciiHexDigit(c)))
            throw new CommandException("CHECKSUM_FORMAT_INVALID", "Request digest must be 64 hexadecimal characters.");
        var hash = SHA256.HashData(bytes);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(digest)))
            throw new CommandException("CHECKSUM_INVALID", "Request digest does not match the uploaded bytes.");
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes); // Reject invalid UTF-8 instead of replacing characters.
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            CheckProperties(json.RootElement);
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a JSON object.");
            return new(json.RootElement.Clone(), Convert.ToHexString(hash).ToLowerInvariant());
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw new CommandException("REQUEST_INVALID", "Request must be valid UTF-8 JSON without duplicate property names."); }
    }

    private static void CheckProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate property.");
                CheckProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckProperties(item);
    }

    public static void Fields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new CommandException("REQUEST_INVALID", "Expected an object.");
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new CommandException("FIELD_UNSUPPORTED", $"Unsupported field: {property.Name}.");
    }

    public static string String(JsonElement value, string name, int max = 256, bool required = true)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new CommandException("REQUEST_INVALID", "Expected an object.");
        if (!value.TryGetProperty(name, out var property) && !required) return "";
        if (property.ValueKind != JsonValueKind.String || property.GetString() is not { } text
            || text.Length > max || (required && string.IsNullOrWhiteSpace(text)))
            throw new CommandException("REQUEST_INVALID", $"{name} must be a string containing {(required ? "1" : "0")}–{max} characters.");
        return text;
    }

    public static decimal? Number(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDecimal(out var number))
            throw new CommandException("REQUEST_INVALID", $"{name} must be a number.");
        return number;
    }

    public static string CanonicalJson(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(value, writer);
        return Encoding.UTF8.GetString(stream.ToArray());
        static void Write(JsonElement item, Utf8JsonWriter writer)
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in item.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Write(property.Value, writer); }
                writer.WriteEndObject();
            }
            else if (item.ValueKind == JsonValueKind.Array)
            { writer.WriteStartArray(); foreach (var child in item.EnumerateArray()) Write(child, writer); writer.WriteEndArray(); }
            else item.WriteTo(writer);
        }
    }
}
