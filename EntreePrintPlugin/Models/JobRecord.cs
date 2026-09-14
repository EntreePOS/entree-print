namespace EntreePrintPlugin.Models;

public sealed record JobRecord
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public string Printer { get; init; } = "";
    public string? RenderId { get; init; }
    public string Status { get; set; } = "accepted";
    public long Version { get; set; } = 1;
    public string? Error { get; set; }
    public string? Detail { get; set; }
    public string? ArtifactPath { get; set; }
    public uint? SpoolerJobId { get; set; }
    public string? SpoolerQueue { get; set; }
    public string? WindowsDocumentName { get; set; }
    public string SpoolerState { get; set; } = "not_submitted";
    public uint? WindowsStatus { get; set; }
    public string? WindowsStatusText { get; set; }
    public string? SpoolerEvidence { get; set; }
    public DateTimeOffset? SpoolerStartedAt { get; set; }
    public DateTimeOffset? SpoolerHandoffCompletedAt { get; set; }
    public DateTimeOffset? SpoolerObservedAt { get; set; }
    public bool SpoolerHandoffUncertain { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public string? RetryReason { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public DateTimeOffset AcceptedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ArtifactExpiresAt { get; set; }
    public DateTimeOffset? ArtifactExpiredAt { get; set; }
    public JobRecord[]? After { get; init; }
}
