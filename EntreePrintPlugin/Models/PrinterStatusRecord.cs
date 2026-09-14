namespace EntreePrintPlugin.Models;

public sealed record PrinterStatusRecord
{
    public required string Name { get; init; }
    public string SystemName { get; init; } = "";
    public string Status { get; init; } = "unknown";
    public bool? Offline { get; init; }
    public bool? PaperOut { get; init; }
    public bool? PaperLow { get; init; }
    public bool Stale { get; init; }
    public bool? CoverOpen { get; init; }
    public bool? DrawerOpen { get; init; }
    public bool? Paused { get; init; }
    public bool? Error { get; init; }
    public bool? Default { get; init; }
    public string DriverName { get; init; } = "";
    public string PortName { get; init; } = "";
    public string HostAddress { get; init; } = "";
    public int? PortNumber { get; init; }
    public int? QueueType { get; init; }
    public string ConnectionServer { get; init; } = "";
    public string PortMonitor { get; init; } = "";
    public int? PortProtocol { get; init; }
    public string LprQueueName { get; init; } = "";
    public string RawPrinterStatus { get; init; } = "";
    public string StatusSource { get; init; } = "windows";
    public int? PrinterState { get; init; }
    public int? DetectedErrorState { get; init; }
    public int? ExtendedPrinterStatus { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
