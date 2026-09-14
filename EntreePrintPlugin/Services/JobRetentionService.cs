namespace EntreePrintPlugin.Services;

public sealed class JobRetentionService(JobStore jobs, ILogger<JobRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { jobs.ExpireArtifacts(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { logger.LogWarning(error, "Receipt cleanup stopped after a storage failure; unprocessed receipts and all intent records remain retained."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
