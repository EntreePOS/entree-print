using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntreePrintPlugin.Tests;

public sealed class TrailingActionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),"EntreeTrailingTests",Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory,true); }
    private static AcceptedCommand Command(string id="receipt") => new(id,"print","Kitchen","","","",null,
        IdempotencyKey:id,After:[new("cut","1D5600"),new("beep","1B420101")]);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10,timeout.Token);
    }

    [Fact]
    public async Task ReceiptCutAndBeepStayTogetherInThePluginQueue()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new Host(directory,async (command,token) =>
        {
            if (command.Id == "one") { entered.SetResult(); await release.Task.WaitAsync(token); }
            return new("completed");
        });
        host.Processor.AcceptValidated(Command("one"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Processor.AcceptValidated(Command("two"));
        release.SetResult();
        await Until(()=>JobStore.DisplayState(host.Jobs.Get("two")!)=="completed");
        Assert.Equal(new[]{"one","one:after:cut","one:after:beep","two","two:after:cut","two:after:beep"},host.Calls);
        Assert.Equal(2,host.Jobs.List().Count); // Action phases are not separate public jobs.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActionFailureNeverRetriesReceiptOrRunsLaterActions(bool knownNotSent)
    {
        await using (var host = new Host(directory,(command,_) => command.Type == "cut"
            ? Task.FromException<PrintExecutionResult>(knownNotSent ? new PrintNotSubmittedException("No handoff") : new IOException("Lost action outcome"))
            : Task.FromResult(new PrintExecutionResult("completed"))))
        {
            host.Processor.AcceptValidated(Command());
            await Until(()=>host.Jobs.Get("receipt")!.After![1].Status=="skipped");
            var receipt=host.Jobs.Get("receipt")!;
            Assert.Equal("completed",receipt.Status);
            Assert.Equal("needs_attention",JobStore.DisplayState(receipt));
            Assert.Equal(knownNotSent ? "failed" : "needs_attention",receipt.After![0].Status);
            Assert.Equal(new[]{"receipt","receipt:after:cut"},host.Calls);
            Assert.False(host.Processor.AcceptValidated(Command()).Created);
        }
        await using var reopened = new Host(directory);
        reopened.Processor.RecoverPendingJobs();
        Assert.Empty(reopened.Jobs.RecoverableJobs());
        Assert.False(reopened.Processor.AcceptValidated(Command()).Created);
        Assert.Empty(reopened.Calls);
    }

    [Fact]
    public async Task ReopenContinuesOnlyKnownUnsentActionsAfterACompletedReceipt()
    {
        await using (var host=new Host(directory))
        {
            var receipt=host.Jobs.Accept(Command(),1).Job;
            host.Jobs.UpdateStatus(receipt,"completed");
        }
        await using var reopened=new Host(directory);
        reopened.Processor.RecoverPendingJobs();reopened.Processor.RecoverPendingJobs();
        await Until(()=>JobStore.DisplayState(reopened.Jobs.Get("receipt")!)=="completed");
        Assert.Equal(new[]{"receipt:after:cut","receipt:after:beep"},reopened.Calls);
        Assert.Equal(0,reopened.Jobs.Get("receipt")!.Attempts);
    }

    [Fact]
    public async Task ReopenWithAnUncertainActionKeepsItsWindowsIdentityAndSkipsTheNextAction()
    {
        string document;
        await using (var host=new Host(directory))
        {
            var receipt=host.Jobs.Accept(Command(),1).Job;host.Jobs.UpdateStatus(receipt,"completed");
            document=host.Jobs.BeginSpoolerSubmission("receipt:after:cut","Kitchen");
            host.Jobs.RecordSpoolerJob("receipt:after:cut",41,document);
        }
        await using var reopened=new Host(directory);
        reopened.Processor.RecoverPendingJobs();
        await Until(()=>reopened.Jobs.Get("receipt")!.After![1].Status=="skipped");
        var phase=Assert.Single(reopened.Jobs.MonitoredJobs());
        Assert.Equal(41u,phase.SpoolerJobId);Assert.Equal(document,phase.WindowsDocumentName);
        Assert.True(phase.SpoolerHandoffUncertain);Assert.Empty(reopened.Calls);
        Assert.Equal("needs_attention",JobStore.DisplayState(reopened.Jobs.Get("receipt")!));
    }

    [Fact]
    public async Task CompletedReceiptAndSubmittedCutResumeOnlyTheBeep()
    {
        await using (var host=new Host(directory))
        {
            var receipt=host.Jobs.Accept(Command(),1).Job;host.Jobs.UpdateStatus(receipt,"completed");
            var document=host.Jobs.BeginSpoolerSubmission("receipt:after:cut","Kitchen");
            host.Jobs.RecordSpoolerJob("receipt:after:cut",41,document);
            host.Jobs.UpdateStatus(host.Jobs.GetDelivery("receipt:after:cut"),"submitted");
        }
        await using var reopened=new Host(directory);reopened.Processor.RecoverPendingJobs();
        await Until(()=>reopened.Jobs.Get("receipt")!.After![1].Status=="completed");
        Assert.Equal(new[]{"receipt:after:beep"},reopened.Calls);
        Assert.Equal("submitted",JobStore.DisplayState(reopened.Jobs.Get("receipt")!));
    }

    [Fact]
    public async Task EachPhaseUsesItsOwnExactWindowsIdentityAndUpdatesTheParentVersion()
    {
        await using var host=new Host(directory);
        var receipt=host.Jobs.Accept(Command(),1).Job;
        host.Jobs.UpdateStatus(receipt,"completed");
        var cut=host.Jobs.GetDelivery("receipt:after:cut");
        var document=host.Jobs.BeginSpoolerSubmission(cut.Id,"Kitchen");
        host.Jobs.RecordSpoolerJob(cut.Id,41,document);host.Jobs.UpdateStatus(cut,"submitted");
        var version=host.Jobs.Get("receipt")!.Version;
        host.Jobs.ApplySpoolerObservation(cut.Id,42,document,0x80,"fixture");
        host.Jobs.ApplySpoolerObservation(cut.Id,41,"other",0x80,"fixture");
        Assert.Equal(version,host.Jobs.Get("receipt")!.Version);
        host.Jobs.ApplySpoolerObservation(cut.Id,41,document,0x80,"fixture");
        var next=host.Jobs.Get("receipt")!;
        Assert.Equal(version+1,next.Version);Assert.Equal("completed",next.After![0].Status);
        Assert.Null(next.SpoolerJobId);Assert.Equal("completed",next.Status);
        next.After[0].Status="tampered";next.After[1]=next.After[0];
        Assert.Equal("completed",host.Jobs.Get("receipt")!.After![0].Status);
        Assert.Equal("beep",host.Jobs.Get("receipt")!.After![1].Type);
        using var saved=JsonDocument.Parse(host.Events.History!.ReadAfter(0).Items[^1].Data.GetRawText());
        Assert.Equal("completed",saved.RootElement.GetProperty("job").GetProperty("After")[0].GetProperty("Status").GetString());
    }

    [Fact]
    public async Task FailedPhasePersistenceCannotAdvanceOrPublishTheAction()
    {
        await using var host=new Host(directory);
        host.Jobs.Accept(Command(),1);
        var original=host.Jobs.Get("receipt")!;var cursor=host.Events.History!.LatestCursor;
        var path=Path.Combine(directory,"jobs",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("receipt")))+".json.tmp");
        Directory.CreateDirectory(path);
        var failure=Record.Exception(()=>host.Jobs.BeginSpoolerSubmission("receipt:after:cut","Kitchen"));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(original.Version,host.Jobs.Get("receipt")!.Version);
        Assert.Equal("queued",host.Jobs.Get("receipt")!.After![0].Status);
        Assert.Equal(cursor,host.Events.History.LatestCursor);
    }

    [Fact]
    public async Task ReceiptRetentionWaitsForEveryActionAndThenStartsFromItsLastCompletion()
    {
        await using var host=await V2EndpointTests.Harness.Start();
        var receipt=host.Jobs.Accept(Command() with {Prepared=host.Preview},1).Job;
        host.Jobs.UpdateStatus(receipt,"completed");
        host.Clock.Now+=TimeSpan.FromDays(8);
        Assert.Equal(0,host.Jobs.ExpireArtifacts());
        host.Jobs.UpdateStatus(host.Jobs.GetDelivery("receipt:after:cut"),"completed");
        host.Jobs.UpdateStatus(host.Jobs.GetDelivery("receipt:after:beep"),"completed");
        Assert.Equal(host.Clock.Now+TimeSpan.FromDays(7),host.Jobs.Get("receipt")!.ArtifactExpiresAt);
        Assert.Equal(0,host.Jobs.ExpireArtifacts());host.Clock.Now+=TimeSpan.FromDays(8);
        Assert.Equal(1,host.Jobs.ExpireArtifacts());
        await host.RestartAsync();
        Assert.Equal("completed",JobStore.DisplayState(host.Jobs.Get("receipt")!));
    }

    [Fact]
    public async Task StorageFailureAfterAnActionDoesNotLetAnotherReceiptOvertakeItsUnresolvedState()
    {
        var temporary=Path.Combine(directory,"jobs",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("one")))+".json.tmp");
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storageBlocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host=new Host(directory,(command,_)=>
        {
            if (command.Id=="one:after:cut") {Directory.CreateDirectory(temporary);entered.SetResult();}
            return Task.FromResult(new PrintExecutionResult("completed"));
        },new StorageWaitLogger(storageBlocked));
        host.Processor.AcceptValidated(Command("one"));await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Processor.AcceptValidated(Command("two"));
        await storageBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[]{"one","one:after:cut"},host.Calls);
        Directory.Delete(temporary);
        await Until(()=>JobStore.DisplayState(host.Jobs.Get("two")!)=="completed");
        Assert.Equal(new[]{"one","one:after:cut","two","two:after:cut","two:after:beep"},host.Calls);
        Assert.Equal("needs_attention",host.Jobs.Get("one")!.After![0].Status);
        Assert.Equal("skipped",host.Jobs.Get("one")!.After![1].Status);
    }

    [Theory]
    [InlineData("{\"openDrawer\":true}")]
    [InlineData("{\"beep\":\"true\"}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"cut\":true}")]
    public async Task InvalidOrDriverManagedAfterIsRejectedBeforeAcceptance(string after)
    {
        await using var host=await V2EndpointTests.Harness.Start(new PluginSettings {AccessToken=new string('t',32),
            PrinterProfiles=new(){["Kitchen"]=new(){CutMode="driver",CutCommandHex="1D5600",BeepCommandHex="1B420101"}}});
        using var response=await host.Client.SendAsync(host.Request(host.PrintBody("one")[..^1]+",\"after\":"+after+"}"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);
        Assert.Empty(host.Jobs.List());Assert.Equal(0,host.Backend.Calls);
    }

    [Fact]
    public async Task ApiTracksOneJobAndFreezesActionBytesAcrossRetryAndRestart()
    {
        var profiles=new Dictionary<string,PrinterProfile>{["Kitchen"]=new(){BeepCommandHex="1B420101"}};
        await using var host=await V2EndpointTests.Harness.Start(new PluginSettings{AccessToken=new string('t',32),PrinterProfiles=profiles});
        var body=host.PrintBody("one")[..^1]+",\"after\":{\"beep\":true}}";
        using var accepted=await host.Client.SendAsync(host.Request(body));Assert.Equal(HttpStatusCode.Accepted,accepted.StatusCode);
        var id=V2ApiService.JobId("one");await Until(()=>host.Jobs.Get(id)!.After![0].Status=="completed");
        Assert.Equal(new[]{"print","beep"},host.Backend.Executed.Select(command=>command.Type));
        Assert.Single(host.Jobs.List());profiles["Kitchen"]=new(){BeepCommandHex="1B420202"};
        await host.RestartAsync();
        using var replay=await host.Client.SendAsync(host.Request(body));Assert.Equal(HttpStatusCode.OK,replay.StatusCode);
        Assert.Equal(2,host.Backend.Calls);
        Assert.Equal("1B420101",host.Jobs.GetCommand(id)!.After![0].Command);
        using var changed=await host.Client.SendAsync(host.Request(host.PrintBody("one")));
        Assert.Equal(HttpStatusCode.Conflict,changed.StatusCode);
        using var view=JsonDocument.Parse(await replay.Content.ReadAsByteArrayAsync());
        Assert.Equal("completed",view.RootElement.GetProperty("after").GetProperty("beep").GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("driver")]
    [InlineData("typo")]
    public async Task RawCutRequiresAnExplicitRawCutPolicy(string mode)
    {
        await using var host=await V2EndpointTests.Harness.Start(new PluginSettings {AccessToken=new string('t',32),
            PrinterProfiles=new(){["Kitchen"]=new(){CutMode=mode,CutCommandHex="1D5600"}}});
        using var response=await host.Client.SendAsync(host.Request(host.PrintBody("cut")[..^1]+",\"after\":{\"cut\":true}}"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);
        Assert.Empty(host.Jobs.List());Assert.Equal(0,host.Backend.Calls);
    }

    private sealed class Host : IAsyncDisposable
    {
        public readonly EventBroadcaster Events;
        public readonly JobStore Jobs;
        public readonly CommandProcessor Processor;
        public readonly ConcurrentQueue<string> Calls=new();
        private readonly PrinterExecutionQueue queue=new(NullLogger<PrinterExecutionQueue>.Instance);
        public Host(string directory,Func<AcceptedCommand,CancellationToken,Task<PrintExecutionResult>>? execute=null,
            ILogger<CommandProcessor>? logger=null)
        {
            Events=new EventBroadcaster(Path.Combine(directory,"events"));Jobs=new JobStore(Events,Path.Combine(directory,"jobs"));
            Processor=new CommandProcessor(new(){PrintRetryMaxAttempts=1,PrintRetryDelayMs=250},Jobs,new Backend(Calls,execute),queue,
                new V2EndpointTests.Inventory(),logger ?? NullLogger<CommandProcessor>.Instance);
        }
        public async ValueTask DisposeAsync(){await queue.StopAsync(CancellationToken.None);queue.Dispose();Jobs.Dispose();Events.Dispose();}
    }
    private sealed class StorageWaitLogger(TaskCompletionSource blocked) : ILogger<CommandProcessor>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? error,Func<TState,Exception?,string> formatter)
        {
            if (formatter(state,error).StartsWith("Waiting to save trailing action state",StringComparison.Ordinal))
                blocked.TrySetResult();
        }
    }
    private sealed class Backend(ConcurrentQueue<string> calls,Func<AcceptedCommand,CancellationToken,Task<PrintExecutionResult>>? execute) : IPrinterBackend
    {
        public Task<PrintExecutionResult> ExecuteAsync(AcceptedCommand command,CancellationToken token)
        {calls.Enqueue(command.Id);return execute?.Invoke(command,token)??Task.FromResult(new PrintExecutionResult("completed"));}
    }
}
