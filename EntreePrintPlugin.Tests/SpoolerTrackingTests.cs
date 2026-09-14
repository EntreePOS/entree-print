using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;

namespace EntreePrintPlugin.Tests;

public sealed class SpoolerTrackingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EntreeSpoolerTests", Guid.NewGuid().ToString("N"));
    private static AcceptedCommand Command() => new("intent", "print", "Kitchen", "<p>中文</p>", "", "", null);

    [Theory]
    [InlineData(0u, "submitted")]
    [InlineData(0x10u, "printing")]
    [InlineData(0x40u, "blocked")]
    [InlineData(0x200u, "blocked")]
    [InlineData(0x100u, "needs_attention")]
    [InlineData(0x4u, "needs_attention")]
    [InlineData(0x1084u, "completed")]
    public void Flags_DistinguishCompletionFromDeletion(uint flags, string expected) =>
        Assert.Equal(expected, WindowsSpoolerMonitor.StateFromFlags(flags));

    [Fact]
    public void EarlyCompletion_WaitsForFullHandoff_AndSurvivesLateUpdates()
    {
        using var jobs = new JobStore(new EventBroadcaster(), _directory);
        var (worker, _) = jobs.Accept(Command(), 2);
        var document = jobs.BeginSpoolerSubmission(worker.Id, "Kitchen");
        jobs.RecordSpoolerJob(worker.Id, 42, document);
        jobs.ApplySpoolerObservation(worker.Id, 42, document, 0x1000, "notification");
        Assert.Equal("submitting", jobs.Get(worker.Id)!.Status);
        jobs.UpdateStatus(worker, "submitted", artifactPath: "preview.html");
        Assert.Equal("completed", jobs.Get(worker.Id)!.Status);
        jobs.ApplySpoolerObservation(worker.Id, 42, document, 0x104, "query");
        jobs.MarkSpoolerUnknown(worker.Id, "now gone");
        jobs.UpdateStatus(worker, "submitted");
        Assert.Equal("completed", jobs.Get(worker.Id)!.SpoolerState);
        Assert.Equal(0x1000u, jobs.Get(worker.Id)!.WindowsStatus);
        Assert.Equal("preview.html", jobs.Get(worker.Id)!.ArtifactPath);
        Assert.Empty(jobs.MonitoredJobs());
    }

    [Fact]
    public void DifferentDocumentOrJobId_CannotCompleteReceipt()
    {
        using var jobs = new JobStore(new EventBroadcaster(), _directory);
        var (job, _) = jobs.Accept(Command(), 2);
        var document = jobs.BeginSpoolerSubmission(job.Id, "Kitchen");
        jobs.RecordSpoolerJob(job.Id, 42, document);
        jobs.UpdateStatus(job, "submitted");
        jobs.ApplySpoolerObservation(job.Id, 42, "a previous document", 0x80, "query");
        jobs.ApplySpoolerObservation(job.Id, 43, document, 0x80, "query");
        Assert.Null(jobs.Get(job.Id)!.WindowsStatus);
        Assert.Equal("submitted", jobs.Get(job.Id)!.Status);
        jobs.MarkSpoolerUnknown(job.Id, "job disappeared");
        Assert.Equal("needs_attention", jobs.Get(job.Id)!.Status);
        Assert.Empty(jobs.RecoverableJobs());
        Assert.False(jobs.Accept(Command(), 2).Created);
    }

    [Fact]
    public void PaperOut_BlocksExistingJob_ThenCompletionRecoversWithoutResubmission()
    {
        using var jobs = new JobStore(new EventBroadcaster(), _directory);
        var (job, _) = jobs.Accept(Command(), 2);
        var document = jobs.BeginSpoolerSubmission(job.Id, "Kitchen");
        jobs.RecordSpoolerJob(job.Id, 42, document);
        jobs.UpdateStatus(job, "submitted");
        jobs.ApplySpoolerObservation(job.Id, 42, document, 0x40, "query", "Out of paper");
        Assert.Equal("blocked", jobs.Get(job.Id)!.Status);
        Assert.Equal("Out of paper", jobs.Get(job.Id)!.WindowsStatusText);
        Assert.Empty(jobs.RecoverableJobs());
        Assert.Throws<InvalidOperationException>(() => jobs.BeginSpoolerSubmission(job.Id, "Kitchen"));
        jobs.ApplySpoolerObservation(job.Id, 42, document, 0x80, "notification");
        Assert.Equal("completed", jobs.Get(job.Id)!.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Restart_ReconcilesKnownJob_ButNeverCallsPartialHandoffComplete(bool handoffCompleted)
    {
        string document;
        using (var jobs = new JobStore(new EventBroadcaster(), _directory))
        {
            var (job, _) = jobs.Accept(Command(), 2);
            document = jobs.BeginSpoolerSubmission(job.Id, "Kitchen");
            jobs.RecordSpoolerJob(job.Id, 42, document);
            if (handoffCompleted) jobs.UpdateStatus(job, "submitted");
        }
        using var recovered = new JobStore(new EventBroadcaster(), _directory);
        Assert.Empty(recovered.RecoverableJobs());
        Assert.Single(recovered.MonitoredJobs());
        recovered.ApplySpoolerObservation("intent", 42, document, 0x1000, "query");
        Assert.Equal(handoffCompleted ? "completed" : "needs_attention", recovered.Get("intent")!.Status);
        Assert.Equal(!handoffCompleted, recovered.Get("intent")!.SpoolerHandoffUncertain);
    }

    [Fact]
    public void FailedPersistence_DoesNotExposeCompletionOrMutatedSnapshot()
    {
        using var jobs = new JobStore(new EventBroadcaster(), _directory);
        var (job, _) = jobs.Accept(Command(), 2);
        job.Status = "completed";
        Assert.Equal("queued", jobs.Get(job.Id)!.Status);
        var document = jobs.BeginSpoolerSubmission(job.Id, "Kitchen");
        jobs.RecordSpoolerJob(job.Id, 42, document);
        jobs.UpdateStatus(job, "submitted");
        Directory.CreateDirectory(Directory.GetFiles(_directory, "*.json").Single() + ".tmp");
        var failure = Record.Exception(() => jobs.ApplySpoolerObservation(job.Id, 42, document, 0x80, "query"));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal("submitted", jobs.Get(job.Id)!.Status);
        Assert.Null(jobs.Get(job.Id)!.WindowsStatus);
    }

    [Fact]
    public void ProvenNoHandoff_AllowsNewAttempt_ExistingWindowsJobDoesNot()
    {
        using var jobs = new JobStore(new EventBroadcaster(), _directory);
        var (job, _) = jobs.Accept(Command(), 2);
        var first = jobs.BeginSpoolerSubmission(job.Id, "Kitchen");
        jobs.RecordSpoolerNotSubmitted(job.Id);
        var second = jobs.BeginSpoolerSubmission(job.Id, "Kitchen");
        Assert.NotEqual(first, second);
        jobs.RecordSpoolerJob(job.Id, 42, second);
        Assert.Throws<InvalidOperationException>(() => jobs.RecordSpoolerNotSubmitted(job.Id));
    }

    [Fact]
    public void NotificationReuse_DropsPreviousCompletionRegardlessOfFieldOrder()
    {
        foreach (var documentFirst in new[] { true, false })
        {
            var buffer = new SpoolerNotificationBuffer();
            buffer.Apply([new(42, Document: "old"), new(42, Flags: 0x80)]);
            SpoolerNotificationBuffer.Entry[] reused = [new(42, Document: "new"), new(42, Flags: 0x104)];
            buffer.Apply(documentFirst ? reused : reused.Reverse());
            Assert.Equal("new", buffer.Get(42)!.Document);
            Assert.Null(buffer.Get(42)!.Flags);
            buffer.Apply([new(42, Flags: 0x10)]);
            Assert.Equal(0x10u, buffer.Get(42)!.Flags);
        }
    }

    [Fact]
    public void NotificationCompletion_IsRetainedUntilDiscarded()
    {
        var buffer = new SpoolerNotificationBuffer();
        buffer.Apply([new(42, Flags: 0x80), new(42, Document: "receipt"), new(42, Flags: 0x104)]);
        Assert.Equal(0x184u, buffer.Get(42)!.Flags);
        buffer.Clear();
        buffer.Apply([new(42, Flags: 0x104)]);
        Assert.Null(buffer.Get(42)!.Document);
        Assert.Equal(0x104u, buffer.Get(42)!.Flags);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
