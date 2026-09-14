using System.Text.Json.Nodes;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;

namespace EntreePrintPlugin.Tests;

public sealed class SpoolerIdentityRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EntreeSpoolerIdentityTests", Guid.NewGuid().ToString("N"));
    private static AcceptedCommand Command() => new("intent", "print", "厨房", "<p>ticket</p>", "", "", null, IdempotencyKey: "intent");

    private string StopBeforeIdWasSaved()
    {
        using var jobs = new JobStore(new EventBroadcaster(), _directory);
        jobs.Accept(Command(), 2);
        return jobs.BeginSpoolerSubmission("intent", "厨房");
    }

    [Theory]
    [InlineData(0u, "submitted")]
    [InlineData(0x10u, "printing")]
    [InlineData(0x40u, "blocked")]
    public void Restart_FindsUnrecordedWindowsId_AndBlocksReprintOfActivePartialJob(uint flags, string state)
    {
        var document = StopBeforeIdWasSaved();
        using (var recovered = new JobStore(new EventBroadcaster(), _directory))
        {
            Assert.Single(recovered.MonitoredJobs());
            Assert.Empty(recovered.RecoverableJobs());
            WindowsSpoolerMonitor.ReconcileUnidentifiedJobs(recovered, "厨房", [new(42, document, flags, "fixture status")]);
            var job = recovered.Get("intent")!;
            Assert.Equal(42u, job.SpoolerJobId);
            Assert.Equal(state, job.SpoolerState);
            Assert.Equal("needs_attention", job.Status);
            Assert.True(job.SpoolerHandoffUncertain);
            Assert.Null(job.SpoolerHandoffCompletedAt);
            Assert.Null(job.CompletedAt);
            Assert.Equal("queue_identity", job.SpoolerEvidence);
            Assert.Equal("JOB_NOT_REPRINTABLE", Assert.Throws<CommandException>(() => recovered.ReprintSource("intent")).Code);
            Assert.Throws<InvalidOperationException>(() => recovered.BeginSpoolerSubmission("intent", "厨房"));
            Assert.False(recovered.Accept(Command(), 2).Created);
        }
        using var reopened = new JobStore(new EventBroadcaster(), _directory);
        Assert.Equal(42u, reopened.Get("intent")!.SpoolerJobId);
        Assert.Empty(reopened.RecoverableJobs());
        reopened.ApplySpoolerObservation("intent", 42, document, 0x80, "query");
        Assert.Equal("needs_attention", reopened.Get("intent")!.Status);
        Assert.Null(reopened.Get("intent")!.CompletedAt);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-document")]
    [InlineData("ambiguous")]
    [InlineData("wrong-queue")]
    public void MissingOrAmbiguousMatch_NeverCreatesAnIdOrMakesTheIntentRetryable(string fault)
    {
        var document = StopBeforeIdWasSaved();
        using var recovered = new JobStore(new EventBroadcaster(), _directory);
        WindowsSpoolerMonitor.QueueJob[] snapshot = fault switch
        {
            "missing" => [],
            "wrong-document" => [new(42, document + " extra", 0x80)],
            "ambiguous" => [new(42, document, 0), new(43, document, 0x80)],
            _ => [new(42, document, 0x80)]
        };
        WindowsSpoolerMonitor.ReconcileUnidentifiedJobs(recovered, fault == "wrong-queue" ? "Other" : "厨房", snapshot);
        Assert.Null(recovered.Get("intent")!.SpoolerJobId);
        Assert.Equal("needs_attention", recovered.Get("intent")!.Status);
        Assert.Empty(recovered.RecoverableJobs());
    }

    [Fact]
    public void LiveCallbackAndRecovery_CanObserveTheSameJobWithoutOverwritingEvidence()
    {
        using var jobs = new JobStore(new EventBroadcaster(), _directory);
        var worker = jobs.Accept(Command(), 2).Job;
        var document = jobs.BeginSpoolerSubmission(worker.Id, "厨房");
        WindowsSpoolerMonitor.ReconcileUnidentifiedJobs(jobs, "厨房", [new(42, document, 0x80)]);
        var version = jobs.Get(worker.Id)!.Version;
        jobs.RecordSpoolerJob(worker.Id, 42, document);
        Assert.Equal(version, jobs.Get(worker.Id)!.Version);
        Assert.Equal("queue_identity", jobs.Get(worker.Id)!.SpoolerEvidence);
        Assert.Equal("submitting", jobs.Get(worker.Id)!.Status);
        jobs.UpdateStatus(worker, "submitted");
        Assert.Equal("completed", jobs.Get(worker.Id)!.Status);
        Assert.False(jobs.Get(worker.Id)!.SpoolerHandoffUncertain);
    }

    [Fact]
    public void ActiveWindowsEvidence_SurvivesTheWorkerReportingPartialFailure()
    {
        using var jobs = new JobStore(new EventBroadcaster(), _directory);
        var worker = jobs.Accept(Command(), 2).Job;
        var document = jobs.BeginSpoolerSubmission(worker.Id, "厨房");
        jobs.RecordSpoolerJob(worker.Id, 42, document);
        jobs.ApplySpoolerObservation(worker.Id, 42, document, 0x10, "notification");
        jobs.UpdateStatus(worker, "needs_attention", "Partial handoff failed");
        Assert.Equal("needs_attention", jobs.Get(worker.Id)!.Status);
        Assert.Equal("printing", jobs.Get(worker.Id)!.SpoolerState);
        Assert.Equal("JOB_NOT_REPRINTABLE", Assert.Throws<CommandException>(() => jobs.ReprintSource(worker.Id)).Code);
        Assert.Empty(jobs.RecoverableJobs());
    }

    [Fact]
    public void BindingRechecksQueueDocumentAndExistingIdAtomically()
    {
        var document = StopBeforeIdWasSaved();
        using var jobs = new JobStore(new EventBroadcaster(), _directory);
        Assert.False(jobs.ReconcileSpoolerJob("intent", "Other", 42, document, 0, null));
        Assert.False(jobs.ReconcileSpoolerJob("intent", "厨房", 42, "other", 0, null));
        Assert.False(jobs.ReconcileSpoolerJob("intent", "厨房", 0, document, 0, null));
        Assert.True(jobs.ReconcileSpoolerJob("intent", "厨房", 42, document, 0, null));
        Assert.False(jobs.ReconcileSpoolerJob("intent", "厨房", 43, document, 0x80, null));
        Assert.Equal(42u, jobs.Get("intent")!.SpoolerJobId);
        Assert.Equal(0u, jobs.Get("intent")!.WindowsStatus);
    }

    [Fact]
    public void FailedIdentityPersistence_DoesNotPublishARecoveredId()
    {
        var document = StopBeforeIdWasSaved();
        using var jobs = new JobStore(new EventBroadcaster(), _directory);
        var version = jobs.Get("intent")!.Version;
        Directory.CreateDirectory(Directory.GetFiles(_directory, "*.json").Single() + ".tmp");
        var error = Record.Exception(() => jobs.ReconcileSpoolerJob("intent", "厨房", 42, document, 0x80, null));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Null(jobs.Get("intent")!.SpoolerJobId);
        Assert.Equal(version, jobs.Get("intent")!.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnresolvedWindowsOwnership_DoesNotStopBeingMonitoredAfterADay(bool knownId)
    {
        using (var jobs = new JobStore(new EventBroadcaster(), _directory))
        {
            var worker = jobs.Accept(Command(), 2).Job;
            var document = jobs.BeginSpoolerSubmission(worker.Id, "厨房");
            if (knownId) jobs.RecordSpoolerJob(worker.Id, 42, document);
        }
        var path = Directory.GetFiles(_directory, "*.json").Single();
        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        saved["Job"]!["SpoolerStartedAt"] = DateTimeOffset.UtcNow.AddDays(-3).ToString("O");
        File.WriteAllText(path, saved.ToJsonString());
        using var reopened = new JobStore(new EventBroadcaster(), _directory);
        Assert.Single(reopened.MonitoredJobs());
        Assert.Empty(reopened.RecoverableJobs());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
