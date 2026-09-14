using System.Text.Json;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;

namespace EntreePrintPlugin.Tests;

public sealed class DurableEventTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "EntreeEventTests", Guid.NewGuid().ToString("N"));
    private string Events => Path.Combine(root, "events");
    private string Jobs => Path.Combine(root, "jobs");
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    private static AcceptedCommand Command(string id = "one") => new(id, "print", "cashier", "", "", "", null,
        IdempotencyKey: id, MetadataJson: "{\"orderID\":\"厨房\"}", RequestDigest: "digest");

    [Fact]
    public void ReplaySurvivesReopenWithTheSameEventIdentityAndImmutableData()
    {
        using (var history = new DurableEventLog(Events))
        {
            var data = new Dictionary<string, string> { ["state"] = "accepted" };
            var entry = history.Append("job", "one", 1, DateTimeOffset.UtcNow, data);
            data["state"] = "changed";
            Assert.Equal(1, entry.Sequence);
            Assert.Equal("accepted", history.ReadAfter(0).Items[0].Data.GetProperty("state").GetString());
        }
        using var reopened = new DurableEventLog(Events);
        var page = reopened.ReadAfter(0);
        Assert.Equal(1, page.NextCursor); Assert.Equal(1, page.LatestCursor);
        Assert.Equal("accepted", page.Items[0].Data.GetProperty("state").GetString());
        Assert.Equal(2, reopened.Append("job", "one", 2, DateTimeOffset.UtcNow, new { state = "completed" }).Sequence);
    }

    [Fact]
    public void BoundedReplayRejectsPrunedAndFutureCursorsWithoutResettingSequence()
    {
        using (var history = new DurableEventLog(Events, 2, 4096))
        {
            for (var version = 1; version <= 3; version++) history.Append("job", "one", version, DateTimeOffset.UtcNow, new { version });
            Assert.Equal("EVENT_CURSOR_EXPIRED", Assert.Throws<CommandException>(() => history.ReadAfter(0)).Code);
            Assert.Equal("EVENT_CURSOR_INVALID", Assert.Throws<CommandException>(() => history.ReadAfter(4)).Code);
            var page = history.ReadAfter(1, 1);
            Assert.Single(page.Items); Assert.Equal(2, page.NextCursor); Assert.Equal(3, page.LatestCursor);
        }
        using var reopened = new DurableEventLog(Events, 2, 4096);
        Assert.Equal(4, reopened.Append("printer", "cashier", 0, DateTimeOffset.UtcNow, new { state = "unknown" }).Version);
    }

    [Fact]
    public void ByteBudgetPrunesOldEventsAndRejectsAnOversizeEvent()
    {
        using var history = new DurableEventLog(Events, 100, 4096);
        for (var version = 1; version <= 4; version++) history.Append("job", "one", version, DateTimeOffset.UtcNow, new { text = new string('x', 1200) });
        Assert.Equal("EVENT_CURSOR_EXPIRED", Assert.Throws<CommandException>(() => history.ReadAfter(0)).Code);
        Assert.True(new FileInfo(Path.Combine(Events,"events.json")).Length <= 4096);
        Assert.Throws<IOException>(() => history.Append("job", "one", 5, DateTimeOffset.UtcNow, new { text = new string('x', 4096) }));
        Assert.Equal(4, history.LatestCursor);
    }

    [Fact]
    public void RetriedVersionKeepsItsEventButConflictingDataIsRejected()
    {
        using var history = new DurableEventLog(Events);
        var first = history.Append("job", "one", 1, DateTimeOffset.UtcNow, new { state = "accepted" });
        Assert.Equal(first.Sequence, history.Append("job", "one", 1, DateTimeOffset.UtcNow, new { state = "accepted" }).Sequence);
        Assert.Throws<InvalidDataException>(() => history.Append("job", "one", 1, DateTimeOffset.UtcNow, new { state = "completed" }));
        Assert.Single(history.ReadAfter(0).Items);
    }

    [Fact]
    public void WriteFailureDoesNotPublishOrAdvanceTheReplayCursor()
    {
        using var events = new EventBroadcaster(Events);
        var reader = events.Subscribe(out _);
        Directory.CreateDirectory(Path.Combine(Events,"events.json.tmp"));
        Assert.IsType<UnauthorizedAccessException>(Record.Exception(() => events.PublishJob(new JobRecord { Id="one",Type="print" },Command())));
        Assert.False(reader.TryRead(out _)); Assert.Equal(0,events.History!.LatestCursor);
    }

    [Fact]
    public void CorruptHistoryIsRetainedAndSecondWriterIsRejected()
    {
        using (var history = new DurableEventLog(Events))
        {
            history.Append("job","one",1,DateTimeOffset.UtcNow,new { state="accepted" });
            Assert.Throws<IOException>(() => new DurableEventLog(Events));
        }
        var path = Path.Combine(Events,"events.json");
        var damaged = File.ReadAllText(path).Replace("accepted","completed"); File.WriteAllText(path,damaged);
        Assert.Throws<InvalidDataException>(() => new DurableEventLog(Events));
        Assert.Equal(damaged,File.ReadAllText(path));
    }

    [Fact]
    public void MissingHistoryCannotResetPreviouslyUsedEventIdentities()
    {
        using (var history = new DurableEventLog(Events)) history.Append("job","one",1,DateTimeOffset.UtcNow,new { state="accepted" });
        File.Delete(Path.Combine(Events,"events.json"));
        Assert.Throws<InvalidDataException>(() => new DurableEventLog(Events));
        Assert.False(File.Exists(Path.Combine(Events,"events.json")));
    }

    [Fact]
    public async Task SlowConsumerReceivesAnExplicitGapInsteadOfSilentDroppedUpdates()
    {
        using var events = new EventBroadcaster();
        var reader = events.Subscribe(out _);
        for (var index=0; index<201; index++) events.Publish("job",index);
        var count=0; while (reader.TryRead(out _)) count++;
        Assert.Equal(200,count);
        await Assert.ThrowsAsync<IOException>(async () => await reader.Completion);
    }

    [Fact]
    public void AcceptedJobRetainsItsPendingEventWhenHistoryCannotBeWritten()
    {
        using var events = new EventBroadcaster(Events);
        using var jobs = new JobStore(events,Jobs);
        var obstruction = Path.Combine(Events,"events.json.tmp"); Directory.CreateDirectory(obstruction);
        var accepted = jobs.Accept(Command(),2);
        Assert.True(accepted.Created); Assert.NotNull(jobs.Get("one"));
        Assert.False(jobs.Accept(Command(),2).Created);
        using (var saved = JsonDocument.Parse(File.ReadAllBytes(Directory.GetFiles(Jobs,"*.json").Single())))
            Assert.Equal(1,saved.RootElement.GetProperty("PendingEvents").GetArrayLength());
        jobs.UpdateStatus(accepted.Job,"submitting");
        Assert.Equal("submitting",jobs.Get("one")!.Status);
        Directory.Delete(obstruction);
        jobs.FlushPendingEvents();
        Assert.Equal(new long[] {1,2},events.History!.ReadAfter(0).Items.Select(item => item.Version));
    }

    [Fact]
    public void RestartReplaysPendingAcceptanceBeforeRecoveringLaterJobStates()
    {
        using (var events = new EventBroadcaster(Events))
        using (var jobs = new JobStore(events,Jobs))
        {
            Directory.CreateDirectory(Path.Combine(Events,"events.json.tmp"));
            jobs.Accept(Command(),2);
        }
        Directory.Delete(Path.Combine(Events,"events.json.tmp"));
        using var recoveredEvents = new EventBroadcaster(Events);
        using var recoveredJobs = new JobStore(recoveredEvents,Jobs);
        var entry = Assert.Single(recoveredEvents.History!.ReadAfter(0).Items);
        Assert.Equal("queued",entry.Data.GetProperty("job").GetProperty("Status").GetString());
        Assert.Equal("{\"orderID\":\"厨房\"}",entry.Data.GetProperty("MetadataJson").GetString());
        Assert.False(recoveredJobs.Accept(Command(),2).Created);
    }

    [Fact]
    public void JobWriteFailureCreatesNeitherAcceptanceNorAnEvent()
    {
        using var events = new EventBroadcaster(Events);
        using var jobs = new JobStore(events,Jobs);
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("one")));
        Directory.CreateDirectory(Path.Combine(Jobs,id+".json.tmp"));
        Assert.Equal("STORAGE_UNAVAILABLE",Assert.Throws<CommandException>(() => jobs.Accept(Command(),2)).Code);
        Assert.Null(jobs.Get("one")); Assert.Equal(0,events.History!.LatestCursor);
    }

    [Fact]
    public void CrashAfterEventCommitBeforeClearingPendingDoesNotInventAnotherVersion()
    {
        using (var events = new EventBroadcaster(Events))
        using (var jobs = new JobStore(events,Jobs)) jobs.Accept(Command(),2);
        var path = Directory.GetFiles(Jobs,"*.json").Single();
        // Recreate the earlier atomic job document left by this crash window.
        var original = File.ReadAllText(path);
        using var document = JsonDocument.Parse(original);
        var pending = original.Replace("\"PendingEvents\":null","\"PendingEvents\":["+document.RootElement.GetProperty("Job").GetRawText()+"]");
        File.WriteAllText(path,pending);
        using var recoveredEvents = new EventBroadcaster(Events);
        using var recoveredJobs = new JobStore(recoveredEvents,Jobs);
        Assert.Single(recoveredEvents.History!.ReadAfter(0).Items);
        using var saved = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(JsonValueKind.Null,saved.RootElement.GetProperty("PendingEvents").ValueKind);
        Assert.Equal(1,recoveredJobs.Get("one")!.Version);
    }

    [Fact]
    public void PendingNotificationBacklogIsBoundedWithoutDiscardingEarlierStates()
    {
        using var events = new EventBroadcaster(Events);
        using var jobs = new JobStore(events,Jobs);
        var obstruction = Path.Combine(Events,"events.json.tmp"); Directory.CreateDirectory(obstruction);
        var accepted = jobs.Accept(Command(),2);
        for (var index=1; index<256; index++) jobs.UpdateStatus(accepted.Job,"waiting_for_printer","offline");
        Assert.Throws<IOException>(() => jobs.UpdateStatus(accepted.Job,"submitting"));
        Assert.Equal(256,jobs.Get("one")!.Version);
        Directory.Delete(obstruction);
        jobs.FlushPendingEvents();
        var first = events.History!.ReadAfter(0);
        Assert.Equal(256,first.LatestCursor); Assert.Equal(1,first.Items[0].Version);
        Assert.Equal(256,events.History.ReadAfter(255).Items[0].Version);
    }

    [Fact]
    public async Task ProductionApiTransitionsAreRetainedAcrossServiceStoreReopen()
    {
        await using var host = await V2EndpointTests.Harness.Start();
        using var response = await host.Client.SendAsync(host.Request(host.PrintBody("events")));
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var id = body.RootElement.GetProperty("id").GetString()!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (host.Jobs.Get(id)!.Status != "completed") await Task.Delay(10,timeout.Token);
        var before = host.Events.History!.ReadAfter(0);
        Assert.Contains(before.Items,item => item.EntityId == id && item.Data.GetProperty("job").GetProperty("Status").GetString() == "completed");
        await host.RestartAsync();
        var recovered = host.Events.History!.ReadAfter(0);
        Assert.Equal(before.LatestCursor,recovered.LatestCursor);
        Assert.Equal(before.Items.Select(item => (item.Sequence,item.EntityId,item.Version)),
            recovered.Items.Select(item => (item.Sequence,item.EntityId,item.Version)));
        Assert.Equal(1,host.Backend.Calls);
    }
}
