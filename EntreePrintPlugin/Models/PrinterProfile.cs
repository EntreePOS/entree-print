namespace EntreePrintPlugin.Models;

public sealed record PrinterProfile
{
    public string CutMode { get; init; } = "driver";
    public string? BeepCommandHex { get; init; }
    public string? DrawerCommandHex { get; init; }
    public string? CutCommandHex { get; init; }
    public bool AllowRawCommands { get; init; }
}
