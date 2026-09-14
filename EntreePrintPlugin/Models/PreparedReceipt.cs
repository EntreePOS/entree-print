using EntreePrintPlugin.Services;

namespace EntreePrintPlugin.Models;

public sealed record PreparedReceipt(string Id, string ServiceId, string Printer, decimal WidthMm,
    string ProfileVersion, string ContentHash, ReceiptTextLayout Layout, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int? Dpi = null);
