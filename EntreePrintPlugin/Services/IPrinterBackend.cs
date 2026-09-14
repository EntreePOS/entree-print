using EntreePrintPlugin.Models;

namespace EntreePrintPlugin.Services;

public interface IPrinterBackend
{
    Task<PrintExecutionResult> ExecuteAsync(AcceptedCommand command, CancellationToken cancellationToken);
}

public sealed record PrintExecutionResult(string Status, string? ArtifactPath = null, string? Detail = null, uint? SpoolerJobId = null);
