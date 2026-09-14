using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace EntreePrintPlugin.Services;

public sealed record JobHistoryQuery(string? Printer, string? Station, string? OrderID, string[] Status,
    DateTimeOffset? Since, int Limit, string? Cursor)
{
    private static readonly string[] States = ["accepted", "waiting_for_printer", "submitting", "submitted", "printing", "blocked", "completed", "failed", "needs_attention"];
    public static JobHistoryQuery Parse(HttpRequest request)
    {
        foreach (var key in request.Query.Keys)
            if (key is not ("printer" or "station" or "orderID" or "status" or "since" or "limit" or "cursor"))
                throw new CommandException("FIELD_UNSUPPORTED", $"Unsupported job filter: {key}.");
        string? Single(string key, int max = 256)
        {
            if (!request.Query.TryGetValue(key, out var values)) return null;
            if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0]!.Length > max)
                throw new CommandException("REQUEST_INVALID", $"{key} must be one nonempty value of at most {max} characters.");
            return values[0];
        }
        var limit = 30;
        if (Single("limit", 3) is { } size && (!int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100))
            throw new CommandException("REQUEST_INVALID", "limit must be between 1 and 100.");
        DateTimeOffset? since = null;
        if (Single("since", 64) is { } date)
        {
            if (!Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$") ||
                !DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new CommandException("REQUEST_INVALID", "since must be an ISO date/time including Z or a time-zone offset.");
            since = parsed.ToUniversalTime();
        }
        var status = request.Query["status"].ToArray();
        if (status.Length > States.Length || status.Any(value => value is null || !States.Contains(value, StringComparer.Ordinal)))
            throw new CommandException("REQUEST_INVALID", "status contains an unsupported job state.");
        return new(Single("printer"), Single("station"), Single("orderID"), status.Select(value => value!).Distinct().Order(StringComparer.Ordinal).ToArray(),
            since, limit, Single("cursor", 2048));
    }

    public JobHistoryCursor? ReadCursor(string serviceId)
    {
        if (Cursor is null) return null;
        try
        {
            var cursor = JsonSerializer.Deserialize<JobHistoryCursor>(WebEncoders.Base64UrlDecode(Cursor));
            if (cursor is null || cursor.Schema != 1 || cursor.ServiceId != serviceId || cursor.Filter != Fingerprint()
                || string.IsNullOrEmpty(cursor.Id) || cursor.Id.Length > 256)
                throw new FormatException();
            return cursor;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        { throw new CommandException("CURSOR_INVALID", "The history cursor is invalid or belongs to different filters or another service."); }
    }

    public string WriteCursor(string serviceId, Models.JobRecord last) => WebEncoders.Base64UrlEncode(
        JsonSerializer.SerializeToUtf8Bytes(new JobHistoryCursor(1, serviceId, Fingerprint(), last.AcceptedAt, last.Id)));

    private string Fingerprint() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { Printer = Printer?.ToUpperInvariant(), Station, OrderID, Status, Since })));
}

public sealed record JobHistoryCursor(int Schema, string ServiceId, string Filter, DateTimeOffset AcceptedAt, string Id);
