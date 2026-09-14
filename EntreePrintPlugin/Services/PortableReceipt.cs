using System.Text.Json;
using System.Text.Json.Serialization;
using EntreePrintPlugin.Models;

namespace EntreePrintPlugin.Services;

// Prepared drawing data only. Importing never claims, transfers or submits a job.
public sealed record PortableReceipt(int Version, string DestinationId, string ComparisonId,
    decimal WidthMm, int Dpi, DateTimeOffset ExpiresAt, ReceiptTextLayout Layout)
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal static PortableReceipt? Create(PreparedReceipt receipt, PrinterStatusRecord printer)
    {
        var destination = NetworkPrinterDestination.From(printer);
        if (printer.Stale || destination is null || receipt.ComparisonId is null || receipt.Dpi is null) return null;
        var artifact = new PortableReceipt(1, destination.Id, receipt.ComparisonId, receipt.WidthMm,
            receipt.Dpi.Value, receipt.ExpiresAt, receipt.Layout);
        // Leave space for the containing request and a queue name under the 1 MB API limit.
        return JsonSerializer.SerializeToUtf8Bytes(artifact, WireOptions).Length <= 900_000 ? artifact : null;
    }

    internal static PortableReceipt Read(JsonElement value)
    {
        try
        {
            var artifact = value.Deserialize<PortableReceipt>(WireOptions)
                ?? throw new JsonException("Artifact must be an object.");
            if (artifact.Version != 1 || !Digest(artifact.DestinationId, "tcpip:") || !Digest(artifact.ComparisonId, "layout:") ||
                artifact.WidthMm is < 20 or > 100 || artifact.Dpi is < 150 or > 1200 ||
                artifact.Layout is not { Text: not null, Rectangles: not null } layout ||
                layout.Text.Any(run => run is null || run.Text is null || string.IsNullOrWhiteSpace(run.Family) || run.Color is null) ||
                layout.Rectangles.Any(box => box is null || box.Color is null) ||
                (layout.KeepTogether?.Any(region => region is null) ?? false) ||
                Math.Abs(layout.Width - (float)(artifact.WidthMm * 96m / 25.4m)) > .5f)
                throw new JsonException("Invalid prepared drawing data.");
            WindowsTextPrinter.Validate(layout);
            return artifact;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new CommandException("ARTIFACT_INVALID", "The portable receipt is invalid or uses an unsupported format.", error); }
    }

    internal void Validate(PrinterStatusRecord printer, PrinterLayoutSettings driver, CancellationToken token)
    {
        if (printer.Stale || NetworkPrinterDestination.From(printer)?.Id != DestinationId)
            throw new CommandException("RENDER_DESTINATION_MISMATCH", "This receipt requires the same directly configured Windows TCP/IP destination.");
        if (driver.DpiX != Dpi || driver.DpiY != Dpi || WidthMm > driver.PrintableWidthMm)
            throw new CommandException("RENDER_CHANGED", "The backup driver's dimensions or resolution differ from the reviewed receipt.");
        ReceiptComparison.Validate(ComparisonId, Layout, printer.DriverName, driver, token);
        // Validate the complete page plan before saving, without invoking Chromium or StartDoc.
        _ = ReceiptPaginator.Paginate(Layout, driver.PrintableWidthDots * 96f / driver.DpiX,
            driver.PrintableHeightDots * 96f / driver.DpiY, Dpi);
    }

    private static bool Digest(string? value, string prefix) => value is not null &&
        value.Length == prefix.Length + 64 && value.StartsWith(prefix, StringComparison.Ordinal) &&
        value[prefix.Length..].All(character => char.IsAsciiHexDigit(character) && !char.IsUpper(character));
}
