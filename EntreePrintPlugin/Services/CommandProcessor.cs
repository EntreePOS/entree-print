
using EntreePrintPlugin.Models;

namespace EntreePrintPlugin.Services;

public sealed class CommandProcessor(
    PluginSettings settings,
    JobStore jobs,
    IPrinterBackend printerBackend,
    PrinterExecutionQueue executionQueue,
    IPrinterInventory printerStatus,
    ILogger<CommandProcessor> logger)
{
    private int _recoveryStarted;
    public void RecoverPendingJobs()
    {
        if (Interlocked.Exchange(ref _recoveryStarted, 1) != 0) return;
        foreach (var (command, job) in jobs.RecoverableJobs())
            executionQueue.Enqueue(command.Printer, token => ProcessAsync(command, job, token));
    }

    public (JobRecord Job, bool Created) AcceptValidated(AcceptedCommand command)
    {
        var accepted = jobs.Accept(command, settings.PrintRetryMaxAttempts);
        if (accepted.Created)
            try
            {
                var persisted = jobs.GetCommand(command.Id)!;
                executionQueue.Enqueue(persisted.Printer, token => ProcessAsync(persisted, accepted.Job, token));
            }
            catch (CommandException error) when (error.Code == "SERVICE_STOPPING")
            { logger.LogInformation("Accepted job {Id} is retained for recovery after shutdown.", command.Id); }
        return accepted;
    }

    private async Task ProcessAsync(AcceptedCommand command, JobRecord record, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var blocker = GetRecoverablePrinterBlocker(command);
            if (blocker is not null)
            {
                // Waiting on an observed device condition is not a failed delivery attempt.
                // Keep the accepted receipt until that condition clears, including across restart.
                if (record.Status != "waiting_for_printer" || record.RetryReason != blocker || record.NextRetryAt is not null)
                {
                    record.RetryReason = blocker;
                    record.NextRetryAt = null;
                    jobs.UpdateStatus(record, "waiting_for_printer", blocker);
                }
                // An unchanged observation need not rewrite the retained receipt or publish another job version.
                await Task.Delay(Math.Max(250, settings.PrintRetryDelayMs), cancellationToken);
                continue;
            }

            try
            {
                record.Attempts++;
                record.RetryReason = null;
                record.NextRetryAt = null;
                jobs.UpdateStatus(record, "submitting");
                var result = await printerBackend.ExecuteAsync(command, cancellationToken);
                record.SpoolerJobId = result.SpoolerJobId;
                jobs.UpdateStatus(record, result.Status, result.Detail, result.ArtifactPath);
                return;
            }
            catch (Exception error)
            {
                // Only explicit evidence of no handoff can justify a second backend call.
                // Error message text cannot establish whether Windows already accepted output.
                var reason = error is PrintNotSubmittedException safe ? safe.Message : null;
                if (reason is not null)
                {
                    if (ShouldRetry(command, record))
                    {
                        logger.LogWarning(error, "Command {Id} will retry after recoverable print failure: {Reason}.", command.Id, reason);
                        await WaitBeforeRetryAsync(record, reason, cancellationToken);
                        continue;
                    }

                    jobs.UpdateStatus(record, "failed", $"Retry limit reached after recoverable print failure: {reason}. Last error: {error.Message}");
                    return;
                }

                jobs.UpdateStatus(record, error is CommandException ? "failed" : "needs_attention", error.Message);
                logger.LogWarning(error, "Command {Id} failed.", command.Id);
                return;
            }
        }
    }

    private async Task WaitBeforeRetryAsync(JobRecord record, string reason, CancellationToken cancellationToken)
    {
        var delayMs = Math.Max(250, settings.PrintRetryDelayMs);
        record.RetryReason = reason;
        record.NextRetryAt = DateTimeOffset.UtcNow.AddMilliseconds(delayMs);
        jobs.UpdateStatus(record, "waiting_for_printer", reason);
        await Task.Delay(delayMs, cancellationToken);
    }

    private bool ShouldRetry(AcceptedCommand command, JobRecord record)
    {
        return command.Type == "print" &&
               (settings.PrintRetryMaxAttempts <= 0 || record.Attempts < settings.PrintRetryMaxAttempts);
    }

    private string? GetRecoverablePrinterBlocker(AcceptedCommand command)
    {
        if (command.Type != "print")
        {
            return null;
        }

        var status = printerStatus.GetCachedPrinter(command.Printer);
        if (status is null || status.Stale)
        {
            return null;
        }

        if (status.PaperOut == true) return "paper_out";
        if (status.CoverOpen == true) return "cover_open";
        if (status.Offline == true) return "offline";
        if (status.Paused == true) return "paused";
        return null;
    }

}
