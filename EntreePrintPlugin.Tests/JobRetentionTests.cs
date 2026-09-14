using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntreePrintPlugin.Tests;

public sealed class JobRetentionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EntreeRetentionTests", Guid.NewGuid().ToString("N"));
    private readonly V2EndpointTests.Clock _clock = new();
    private JobStore Store((int Jobs, int PreparedJobs, long Bytes)? limits = null) =>
        new(new EventBroadcaster(), _directory, _clock, 7, limits ?? (100, 50, 1_000_000));

    private AcceptedCommand Command(string key = "original", int characters = 5000)
    {
        var layout = new ReceiptTextLayout(272, 60, [new(new string('A', characters), 0, 20, 120, 16, "Arial", false, false, "#000")], []);
        var hash = PreparedReceiptStore.Hash("Kitchen", 72, "driver", layout, 203);
        var receipt = new PreparedReceipt(Guid.NewGuid().ToString("D"), "service", "Kitchen", 72, "driver", hash,
            layout, _clock.Now, _clock.Now.AddMinutes(30), 203);
        return new(V2ApiService.JobId(key), "print", "Kitchen", "", "", "", null, receipt, key,
            "{\"station\":\"POS-1\",\"orderID\":\"42\"}", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
    }

    [Fact]
    public void ExpiryStartsAtCompletionAndCompactionKeepsIdentityAcrossRestart()
    {
        var command = Command();
        long before;
        using (var jobs = Store())
        {
            var job = jobs.Accept(command, 1).Job;
            _clock.Now = _clock.Now.AddDays(30);
            Assert.Equal(0, jobs.ExpireArtifacts());
            jobs.UpdateStatus(job, "completed");
            Assert.Equal(_clock.Now.AddDays(7), jobs.Get(job.Id)!.ArtifactExpiresAt);
            _clock.Now = _clock.Now.AddDays(7).AddTicks(-1);
            Assert.Equal(0, jobs.ExpireArtifacts());
            before = new FileInfo(Directory.GetFiles(_directory, "*.json").Single()).Length;
            _clock.Now = _clock.Now.AddTicks(1);
            Assert.Equal(1, jobs.ExpireArtifacts());
            Assert.Equal(0, jobs.ExpireArtifacts());
            Assert.Null(jobs.GetCommand(job.Id)!.Prepared);
            Assert.True(new FileInfo(Directory.GetFiles(_directory, "*.json").Single()).Length < before / 2);
        }
        using var reopened = Store();
        var retained = reopened.Get(command.Id)!;
        Assert.Equal("completed", retained.Status);
        Assert.Equal(command.Prepared!.Id, retained.RenderId);
        Assert.NotNull(retained.ArtifactExpiredAt);
        Assert.Equal(command.MetadataJson, reopened.GetCommand(command.Id)!.MetadataJson);
        Assert.False(reopened.Accept(command, 1).Created);
        Assert.Equal(command.Id, reopened.ReplayArchived(command.Id, command.RequestDigest!)!.Id);
        Assert.Equal("IDEMPOTENCY_CONFLICT", Assert.Throws<CommandException>(() => reopened.ReplayArchived(command.Id, new string('0', 64))).Code);
        Assert.Equal("IDEMPOTENCY_CONFLICT", Assert.Throws<CommandException>(() => reopened.Accept(command with { Printer = "Other", Prepared = null }, 1)).Code);
        Assert.Equal("ARTIFACT_EXPIRED", Assert.Throws<CommandException>(() => reopened.ReprintSource(command.Id)).Code);
        Assert.Empty(reopened.RecoverableJobs());
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("waiting_for_printer")]
    [InlineData("submitting")]
    [InlineData("submitted")]
    [InlineData("printing")]
    [InlineData("blocked")]
    [InlineData("needs_attention")]
    public void PendingAndUncertainReceiptsNeverExpire(string state)
    {
        using var jobs = Store();
        var command = Command();
        var job = jobs.Accept(command, 1).Job;
        jobs.UpdateStatus(job, state);
        _clock.Now = _clock.Now.AddYears(10);
        Assert.Equal(0, jobs.ExpireArtifacts());
        Assert.NotNull(jobs.GetCommand(job.Id)!.Prepared);
        Assert.Null(jobs.Get(job.Id)!.ArtifactExpiresAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WindowsCompletionExpiresOnlyAfterFullHandoff(bool fullHandoff)
    {
        using var jobs = Store();
        var command = Command();
        var job = jobs.Accept(command, 1).Job;
        var document = jobs.BeginSpoolerSubmission(job.Id, "Kitchen");
        jobs.RecordSpoolerJob(job.Id, 42, document);
        jobs.UpdateStatus(job, fullHandoff ? "submitted" : "needs_attention");
        jobs.ApplySpoolerObservation(job.Id, 42, document, 0x80, "fixture");
        _clock.Now = _clock.Now.AddDays(8);
        Assert.Equal(fullHandoff ? 1 : 0, jobs.ExpireArtifacts());
        Assert.Equal(fullHandoff, jobs.GetCommand(job.Id)!.Prepared is null);
        Assert.Equal(42u, jobs.Get(job.Id)!.SpoolerJobId);
        Assert.Equal(document, jobs.Get(job.Id)!.WindowsDocumentName);
    }

    [Fact]
    public void StorageFailurePreservesOriginalReceiptAndReplay()
    {
        var command = Command();
        using (var jobs = Store())
        {
            var job = jobs.Accept(command, 1).Job;
            jobs.UpdateStatus(job, "failed", "Proven pre-submission failure");
            _clock.Now = _clock.Now.AddDays(8);
            var file = Directory.GetFiles(_directory, "*.json").Single();
            var before = File.ReadAllBytes(file);
            Directory.CreateDirectory(file + ".tmp");
            Assert.True(Record.Exception(() => jobs.ExpireArtifacts()) is IOException or UnauthorizedAccessException);
            Assert.Equal(before, File.ReadAllBytes(file));
            Assert.NotNull(jobs.GetCommand(job.Id)!.Prepared);
            Assert.Null(jobs.Get(job.Id)!.ArtifactExpiredAt);
            Directory.Delete(file + ".tmp");
        }
        using var reopened = Store();
        Assert.False(reopened.Accept(command, 1).Created);
        Assert.Equal(1, reopened.ExpireArtifacts());
    }

    [Fact]
    public async Task ConcurrentCleanupRespectsBatchLimitAndExpiresEachReceiptOnce()
    {
        using var jobs = Store();
        for (var i = 0; i < 5; i++) jobs.UpdateStatus(jobs.Accept(Command("job-" + i), 1).Job, "completed");
        _clock.Now = _clock.Now.AddDays(8);
        Assert.Equal(2, jobs.ExpireArtifacts(2));
        var counts = await Task.WhenAll(Task.Run(() => jobs.ExpireArtifacts()), Task.Run(() => jobs.ExpireArtifacts()));
        Assert.Equal(3, counts.Sum());
        Assert.Equal(5, jobs.List().Count(job => job.ArtifactExpiredAt.HasValue));
    }

    [Fact]
    public void CompactRecordsFreePreparedCapacityButNeverForgetKeysAtLedgerLimit()
    {
        using var jobs = Store((3, 1, 100_000));
        var first = Command();
        var second = Command("second");
        jobs.UpdateStatus(jobs.Accept(first, 1).Job, "completed");
        Assert.Equal("QUEUE_FULL", Assert.Throws<CommandException>(() => jobs.Accept(second, 1)).Code);
        Assert.False(jobs.Accept(first, 1).Created);
        _clock.Now = _clock.Now.AddDays(8);
        Assert.Equal(1, jobs.ExpireArtifacts());
        jobs.UpdateStatus(jobs.Accept(second, 1).Job, "completed");
        _clock.Now = _clock.Now.AddDays(8);
        jobs.ExpireArtifacts();
        jobs.UpdateStatus(jobs.Accept(Command("third"), 1).Job, "completed");
        _clock.Now = _clock.Now.AddDays(8);
        jobs.ExpireArtifacts();
        Assert.Equal("QUEUE_FULL", Assert.Throws<CommandException>(() => jobs.Accept(Command("fourth"), 1)).Code);
        Assert.False(jobs.Accept(first, 1).Created);
        Assert.Equal(3, jobs.List().Count);
    }

    [Fact]
    public void ByteBudgetIsRestoredByCompactionAndReconstructedOnRestart()
    {
        var first = Command(characters: 20000);
        var second = Command("second", 20000);
        using (var jobs = Store((10, 10, 35000)))
        {
            jobs.UpdateStatus(jobs.Accept(first, 1).Job, "completed");
            Assert.Equal("QUEUE_FULL", Assert.Throws<CommandException>(() => jobs.Accept(second, 1)).Code);
        }
        using var reopened = Store((10, 10, 35000));
        Assert.Equal("QUEUE_FULL", Assert.Throws<CommandException>(() => reopened.Accept(second, 1)).Code);
        _clock.Now = _clock.Now.AddDays(8);
        Assert.Equal(1, reopened.ExpireArtifacts());
        Assert.True(reopened.Accept(second, 1).Created);
    }

    [Fact]
    public void CorruptedCompactedContentIdentityFailsStartup()
    {
        using (var jobs = Store())
        {
            jobs.UpdateStatus(jobs.Accept(Command(), 1).Job, "completed");
            _clock.Now = _clock.Now.AddDays(8);
            jobs.ExpireArtifacts();
        }
        var file = Directory.GetFiles(_directory, "*.json").Single();
        var data = JsonNode.Parse(File.ReadAllText(file))!;
        data["ArchivedContentHash"] = new string('0', 64);
        File.WriteAllText(file, data.ToJsonString());
        Assert.Throws<InvalidDataException>(() => Store());
    }

    [Fact]
    public async Task HostedCleanupCompactsOldFinishedReceiptWithoutTraffic()
    {
        using var jobs = Store();
        var command = Command();
        jobs.UpdateStatus(jobs.Accept(command, 1).Job, "completed");
        _clock.Now = _clock.Now.AddDays(8);
        using var worker = new JobRetentionService(jobs, NullLogger<JobRetentionService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(deadline.Token);
        try
        {
            while (jobs.Get(command.Id)!.ArtifactExpiredAt is null) await Task.Delay(10, deadline.Token);
            Assert.NotNull(jobs.ReplayArchived(command.Id, command.RequestDigest!));
        }
        finally { await worker.StopAsync(deadline.Token); }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("366")]
    [InlineData("not-a-number")]
    public void InvalidRetentionConfigurationFailsClearly(string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ReceiptRetentionDays"] = value }).Build();
        Assert.Throws<ArgumentException>(() => PluginSettings.FromConfiguration(config));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
