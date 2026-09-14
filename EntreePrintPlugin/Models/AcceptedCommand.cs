using System.Text.Json.Serialization;

namespace EntreePrintPlugin.Models;

public sealed record AcceptedCommand(
    string Id,
    string Type,
    string Printer,
    string Html,
    string Style,
    string Command,
    string? ContentJson,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedReceipt? Prepared = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IdempotencyKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MetadataJson = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestDigest = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReprintOf = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TrailingCommand[]? After = null);

public sealed record TrailingCommand(string Type, string Command);
