using System.Text;
using System.Text.Json;
using EntreePrintPlugin;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntreePrintPlugin.Tests;

public sealed class DurableJobTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EntreeDurableTests", Guid.NewGuid().ToString("N"));
    private static AcceptedCommand Command(string id = "intent", string html = "<p>中文 ticket</p>") => new(id, "print", "Kitchen", html, "", "", null);

    [Fact]
    public void Restart_PreservesUnsentPayloadAndCompletedReplay()
    {
        using (var original = new JobStore(new EventBroadcaster(), _directory))
        {
            original.Accept(Command("pending"), 2);
            var (done, _) = original.Accept(Command("done"), 2);
            original.UpdateStatus(done, "completed");
        }
        using var recovered = new JobStore(new EventBroadcaster(), _directory);
        Assert.Equal("中文 ticket", recovered.RecoverableJobs().Single().Command.Html.Replace("<p>", "").Replace("</p>", ""));
        Assert.False(recovered.Accept(Command("done"), 2).Created);
        Assert.Equal("completed", recovered.Get("done")!.Status);
        Assert.Equal("IDEMPOTENCY_CONFLICT", Assert.Throws<CommandException>(() => recovered.Accept(Command("done", "changed"), 2)).Code);
    }

    [Fact]
    public void Restart_DuringHandoffRequiresAttentionAndNeverRequeues()
    {
        using (var original = new JobStore(new EventBroadcaster(), _directory))
        {
            var (job, _) = original.Accept(Command(), 2);
            original.UpdateStatus(job, "submitting");
        }
        using var recovered = new JobStore(new EventBroadcaster(), _directory);
        Assert.Equal("needs_attention", recovered.Get("intent")!.Status);
        Assert.Empty(recovered.RecoverableJobs());
        Assert.False(recovered.Accept(Command(), 2).Created);
    }

    [Fact]
    public async Task ConcurrentAcceptance_HasOneDurableOwner()
    {
        using var jobs = new JobStore(new EventBroadcaster(), _directory);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => jobs.Accept(Command(), 2))));
        Assert.Single(results, result => result.Created);
        Assert.Single(Directory.GetFiles(_directory, "*.json"));
    }

    [Fact]
    public void CorruptLedgerAndSecondProcessOwner_FailClosed()
    {
        using (var jobs = new JobStore(new EventBroadcaster(), _directory))
        {
            jobs.Accept(Command(), 2);
            Assert.Throws<IOException>(() => new JobStore(new EventBroadcaster(), _directory));
        }
        File.WriteAllText(Directory.GetFiles(_directory, "*.json").Single(), "broken");
        Assert.Throws<JsonException>(() => new JobStore(new EventBroadcaster(), _directory));
    }

    [Fact]
    public void StorageFailure_DoesNotAcknowledgeOrExposeJob()
    {
        using var jobs = new JobStore(new EventBroadcaster(), _directory);
        var fileId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("intent")));
        Directory.CreateDirectory(Path.Combine(_directory, fileId + ".json.tmp"));
        Assert.Equal("STORAGE_UNAVAILABLE", Assert.Throws<CommandException>(() => jobs.Accept(Command(), 2)).Code);
        Assert.Null(jobs.Get("intent"));
    }

    [Fact]
    public async Task LostNetworkAfterPossibleSubmission_IsNotRetried()
    {
        var events = new EventBroadcaster();
        using var jobs = new JobStore(events, _directory);
        var settings = new PluginSettings { PrintRetryMaxAttempts = 3, PrintRetryDelayMs = 1 };
        var backend = new UncertainBackend();
        var status = new PrinterStatusService(settings, events, NullLogger<PrinterStatusService>.Instance);
        var processor = new CommandProcessor(settings, jobs, backend,
            new PrinterExecutionQueue(NullLogger<PrinterExecutionQueue>.Instance), status, NullLogger<CommandProcessor>.Instance);
        processor.AcceptValidated(Command());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (jobs.Get("intent")?.Status != "needs_attention") await Task.Delay(10, timeout.Token);
        processor.AcceptValidated(Command());
        Assert.Equal(1, backend.Calls);
        Assert.Equal("needs_attention", jobs.Get("intent")!.Status);
    }

    private sealed class UncertainBackend : IPrinterBackend
    {
        public int Calls;
        public Task<PrintExecutionResult> ExecuteAsync(AcceptedCommand command, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            throw new IOException("network timeout after Windows may have accepted bytes");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
