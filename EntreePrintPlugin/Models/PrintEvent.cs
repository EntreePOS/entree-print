namespace EntreePrintPlugin.Models;

public sealed record PrintEvent
{
    public required string Type { get; init; }
    public object? Data { get; init; }
    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;
}
