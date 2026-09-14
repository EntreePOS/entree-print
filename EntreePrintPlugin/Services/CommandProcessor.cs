
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
        if (record.Status is not ("queued" or "accepted" or "waiting_for_printer"))
        {
            await ProcessTrailingAsync(command, cancellationToken);
            return; // Recovery of trailing work must never enter receipt submission again.
        }
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
                await ProcessTrailingAsync(command, cancellationToken);
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
                    await ProcessTrailingAsync(command, cancellationToken);
                    return;
                }

                jobs.UpdateStatus(record, error is CommandException ? "failed" : "needs_attention", error.Message);
                await ProcessTrailingAsync(command, cancellationToken);
                logger.LogWarning(error, "Command {Id} failed.", command.Id);
                return;
            }
        }
    }

    private async Task ProcessTrailingAsync(AcceptedCommand command, CancellationToken token)
    {
        if (command.After is null) return;
        while (!token.IsCancellationRequested)
        {
            try { await ProcessTrailingCoreAsync(command, token); return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Keep the per-printer queue position while phase state cannot be
                // saved. A later receipt must not overtake recoverable trailing work.
                logger.LogWarning(error, "Waiting to save trailing action state for {Id}.", command.Id);
                try { await Task.Delay(Math.Max(250,settings.PrintRetryDelayMs),token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            }
        }
    }

    private async Task ProcessTrailingCoreAsync(AcceptedCommand command, CancellationToken token)
    {
        if (command.After is null) return;
        var receipt = jobs.GetDelivery(command.Id);
        var canContinue = receipt.Status is "submitted" or "printing" or "blocked" or "completed"
            && !receipt.SpoolerHandoffUncertain
            && (receipt.WindowsDocumentName is null || receipt.SpoolerHandoffCompletedAt.HasValue);
        foreach (var action in command.After)
        {
            var phase = jobs.GetDelivery(JobStore.PhaseId(command.Id, action.Type));
            if (phase.Status != "queued")
            {
                if (phase.Status == "submitting")
                    jobs.UpdateStatus(phase,"needs_attention","Action handoff could not be recorded completely; reconcile its Windows identity before another action.");
                canContinue &= phase.Status is "submitted" or "printing" or "blocked" or "completed"
                    && !phase.SpoolerHandoffUncertain
                    && (phase.WindowsDocumentName is null || phase.SpoolerHandoffCompletedAt.HasValue);
                continue;
            }
            if (!canContinue)
            {
                jobs.UpdateStatus(phase, "skipped", "An earlier delivery phase failed or is uncertain; this action was not sent.");
                continue;
            }
            if (token.IsCancellationRequested) return; // Still queued, safe to recover after restart.
            phase.Attempts = 1;
            jobs.UpdateStatus(phase, "submitting");
            try
            {
                var result = await printerBackend.ExecuteAsync(new AcceptedCommand(phase.Id, action.Type, command.Printer,
                    "", "", action.Command, null), token);
                phase.SpoolerJobId = result.SpoolerJobId;
                jobs.UpdateStatus(phase, result.Status, result.Detail, result.ArtifactPath);
                var saved = jobs.GetDelivery(phase.Id);
                canContinue = saved.Status is "submitted" or "printing" or "blocked" or "completed"
                    && !saved.SpoolerHandoffUncertain
                    && (saved.WindowsDocumentName is null || saved.SpoolerHandoffCompletedAt.HasValue);
            }
            catch (Exception error)
            {
                // No automatic retry for a trailing action, and never retry the receipt because of it.
                jobs.UpdateStatus(phase, error is PrintNotSubmittedException or CommandException ? "failed" : "needs_attention", error.Message);
                canContinue = false;
                logger.LogWarning(error, "Trailing {Type} for job {Id} needs attention.", action.Type, command.Id);
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
