using System.Net;
using System.Net.ServerSentEvents;
using System.Text.Json;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;

namespace EntreePrintPlugin.Tests;

public sealed class EventStreamTests
{
    private static AcceptedCommand Command() => new("stream-job", "print", "厨房", "", "", "", null,
        IdempotencyKey:"stable-key", MetadataJson:"{\"orderID\":\"订单42\"}", RequestDigest:"digest");

    [Theory]
    [InlineData("/api/events")]
    [InlineData("/api/events/checkpoint")]
    public async Task EventReadsRequireAuthorization(string path)
    {
        await using var host = await V2EndpointTests.Harness.Start();
        host.Client.DefaultRequestHeaders.Authorization = null;
        using var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
        Assert.Equal(0,host.Events.SubscriberCount);
        Assert.Equal(0,host.Backend.Calls);
    }

    [Fact]
    public async Task CheckpointAndReplayPreserveOriginalJobVersionsWithoutDispatching()
    {
        await using var host = await V2EndpointTests.Harness.Start();
        var job = host.Jobs.Accept(Command(),2).Job;
        host.Jobs.UpdateStatus(job,"completed");
        using var checkpoint = JsonDocument.Parse(await host.Client.GetStringAsync("/api/events/checkpoint"));
        Assert.Equal(EventStream.Cursor(host.Identity.ServiceId,2),checkpoint.RootElement.GetProperty("cursor").GetString());
        await using var stream = await Reader.Open(host,EventStream.Cursor(host.Identity.ServiceId,0));
        Assert.Equal("ready",(await stream.Next()).EventType);
        var accepted = await stream.Next(); var completed = await stream.Next();
        Assert.Equal("job",accepted.EventType);
        using var first = JsonDocument.Parse(accepted.Data); using var second = JsonDocument.Parse(completed.Data);
        Assert.Equal("accepted",first.RootElement.GetProperty("data").GetProperty("state").GetString());
        Assert.Equal("completed",second.RootElement.GetProperty("data").GetProperty("state").GetString());
        Assert.Equal(1,first.RootElement.GetProperty("version").GetInt64());
        Assert.Equal("订单42",first.RootElement.GetProperty("data").GetProperty("metadata").GetProperty("orderID").GetString());
        Assert.Equal(accepted.EventId,first.RootElement.GetProperty("id").GetString());
        Assert.Equal(host.Identity.ServiceId,first.RootElement.GetProperty("serviceId").GetString());
        Assert.Equal(0,host.Backend.Calls);
    }

    [Fact]
    public async Task LastEventIdResumesAfterStoreReopenAndKeepsTheSameService()
    {
        await using var host = await V2EndpointTests.Harness.Start();
        var job = host.Jobs.Accept(Command(),2).Job;
        host.Jobs.UpdateStatus(job,"completed");
        var cursor = EventStream.Cursor(host.Identity.ServiceId,1);
        await host.RestartAsync();
        await using var stream = await Reader.Open(host,cursor,header:true);
        await stream.Next(); var entry = await stream.Next();
        Assert.Equal(EventStream.Cursor(host.Identity.ServiceId,2),entry.EventId);
        using var data = JsonDocument.Parse(entry.Data);
        Assert.Equal("completed",data.RootElement.GetProperty("data").GetProperty("state").GetString());
        Assert.Equal(0,host.Backend.Calls);
    }

    [Fact]
    public async Task LivePrinterObservationUsesThePublicShapeCapturedAtPublication()
    {
        var settings = new PluginSettings { AccessToken = new string('t',32) };
        await using var host = await V2EndpointTests.Harness.Start(settings);
        await using var stream = await Reader.Open(host,null);
        await stream.Next();
        Assert.Equal("caught-up",(await stream.Next()).EventType);
        host.Events.Publish("printer",new PrinterStatusRecord { Name="厨房",Status="offline",Offline=true,PaperOut=null,Stale=false,PortName="IP_queue",HostAddress="192.168.1.80" });
        var entry = await stream.Next();
        using var data = JsonDocument.Parse(entry.Data);
        var printer = data.RootElement.GetProperty("data");
        Assert.Equal("printer",entry.EventType); Assert.Equal("厨房",printer.GetProperty("name").GetString());
        Assert.Equal("network",printer.GetProperty("connection").GetProperty("type").GetString());
        Assert.True(printer.GetProperty("status").GetProperty("offline").GetBoolean());
        Assert.Equal(JsonValueKind.Null,printer.GetProperty("status").GetProperty("paperOut").ValueKind);
        Assert.Equal(0,host.Backend.Calls);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("01")]
    [InlineData("")]
    [InlineData("9223372036854775808")]
    [InlineData("1")]
    public async Task InvalidOrFutureCursorFailsBeforeStreaming(string number)
    {
        await using var host = await V2EndpointTests.Harness.Start();
        using var response = await host.Client.GetAsync("/api/events?cursor="+Uri.EscapeDataString(host.Identity.ServiceId+":"+number));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);
        Assert.Equal(0,host.Events.SubscriberCount);
    }

    [Fact]
    public async Task WrongOwnerDuplicateAndUnknownOptionsAreRejected()
    {
        await using var host = await V2EndpointTests.Harness.Start();
        using var mismatch = await host.Client.GetAsync("/api/events?cursor=other:0");
        Assert.Equal(HttpStatusCode.Conflict,mismatch.StatusCode);
        var cursor = Uri.EscapeDataString(EventStream.Cursor(host.Identity.ServiceId,0));
        using var duplicate = await host.Client.GetAsync($"/api/events?cursor={cursor}&cursor={cursor}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity,duplicate.StatusCode);
        using var token = await host.Client.GetAsync("/api/events?token=not-an-authorized-query-token");
        Assert.Equal(HttpStatusCode.UnprocessableEntity,token.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get,"/api/events?cursor="+cursor);
        request.Headers.Add("Last-Event-ID",EventStream.Cursor(host.Identity.ServiceId,0));
        using var both = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,both.StatusCode);
        Assert.Equal(0,host.Events.SubscriberCount);
    }

    [Fact]
    public async Task PrunedCursorReturnsAnExplicitResynchronizationError()
    {
        await using var host = await V2EndpointTests.Harness.Start(eventCapacity:2);
        for (var index=0; index<3; index++) host.Events.Publish("printer",new PrinterStatusRecord { Name="cashier" });
        using var response = await host.Client.GetAsync("/api/events?cursor="+Uri.EscapeDataString(EventStream.Cursor(host.Identity.ServiceId,0)));
        Assert.Equal(HttpStatusCode.Gone,response.StatusCode);
        using var data = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("EVENT_CURSOR_EXPIRED",data.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0,host.Events.SubscriberCount);
    }

    [Fact]
    public async Task DisconnectReleasesTheServerSubscription()
    {
        await using var host = await V2EndpointTests.Harness.Start();
        var stream = await Reader.Open(host,null);
        await stream.Next(); Assert.Equal(1,host.Events.SubscriberCount);
        await stream.DisposeAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (host.Events.SubscriberCount != 0) await Task.Delay(10,deadline.Token);
    }

    [Fact]
    public async Task InventoryRemovalAndCheckpointDescribeTheSameCommittedInventory()
    {
        await using var host = await V2EndpointTests.Harness.Start();
        var before = host.Inventory.Items;
        var after = new[] { new PrinterStatusRecord { Name = "厨房" } };
        host.Events.PublishInventory(before, after, () => host.Inventory.Items = after);
        using var checkpoint = JsonDocument.Parse(await host.Client.GetStringAsync("/api/events/checkpoint"));
        Assert.Equal("厨房",checkpoint.RootElement.GetProperty("printers")[0].GetProperty("name").GetString());
        Assert.Equal(EventStream.Cursor(host.Identity.ServiceId,2),checkpoint.RootElement.GetProperty("cursor").GetString());
        await using var stream = await Reader.Open(host,EventStream.Cursor(host.Identity.ServiceId,0));
        await stream.Next();
        using var removed = JsonDocument.Parse((await stream.Next()).Data);
        Assert.True(removed.RootElement.GetProperty("data").GetProperty("removed").GetBoolean());
        using var added = JsonDocument.Parse((await stream.Next()).Data);
        Assert.Equal("厨房",added.RootElement.GetProperty("data").GetProperty("name").GetString());
    }

    [Fact]
    public async Task FailedInventoryPublicationCannotYieldASnapshotThatSkipsItsMissingEvents()
    {
        await using var host = await V2EndpointTests.Harness.Start();
        var before = host.Inventory.Items;
        var temporary = Path.Combine(host.DirectoryPath,"events","events.json.tmp");
        Directory.CreateDirectory(temporary);
        var error = Record.Exception(() => host.Events.PublishInventory(before, [], () => host.Inventory.Items = []));
        Assert.True(error is IOException or UnauthorizedAccessException);
        using var failed = await host.Client.GetAsync("/api/events/checkpoint");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,failed.StatusCode);
        Assert.Same(before,host.Inventory.Items);
        Directory.Delete(temporary);
        host.Events.PublishInventory(before, [], () => host.Inventory.Items = []);
        using var recovered = JsonDocument.Parse(await host.Client.GetStringAsync("/api/events/checkpoint"));
        Assert.Empty(recovered.RootElement.GetProperty("printers").EnumerateArray());
        Assert.Equal(EventStream.Cursor(host.Identity.ServiceId,1),recovered.RootElement.GetProperty("cursor").GetString());
    }

    private sealed class Reader(HttpResponseMessage response, Stream stream, CancellationTokenSource lifetime,
        IAsyncEnumerator<SseItem<string>> entries) : IAsyncDisposable
    {
        public static async Task<Reader> Open(V2EndpointTests.Harness host,string? cursor,bool header=false)
        {
            var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get,"/api/events"+(cursor is not null && !header ? "?cursor="+Uri.EscapeDataString(cursor) : ""));
            if (header) request.Headers.Add("Last-Event-ID",cursor);
            var response = await host.Client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,lifetime.Token);
            response.EnsureSuccessStatusCode();
            Assert.Equal("text/event-stream",response.Content.Headers.ContentType!.MediaType);
            var stream = await response.Content.ReadAsStreamAsync(lifetime.Token);
            return new Reader(response,stream,lifetime,SseParser.Create(stream).EnumerateAsync(lifetime.Token).GetAsyncEnumerator());
        }
        public async Task<SseItem<string>> Next()
        {
            Assert.True(await entries.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            return entries.Current;
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel(); await entries.DisposeAsync(); await stream.DisposeAsync(); response.Dispose(); lifetime.Dispose();
        }
    }
}
