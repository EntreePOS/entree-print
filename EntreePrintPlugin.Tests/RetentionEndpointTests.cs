using System.Net;
using System.Text.Json;
using EntreePrintPlugin.Services;

namespace EntreePrintPlugin.Tests;

public sealed class RetentionEndpointTests
{
    private static async Task WaitCompleted(V2EndpointTests.Harness host, string key)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (host.Jobs.Get(V2ApiService.JobId(key))?.Status != "completed")
            await Task.Delay(10, deadline.Token);
    }

    private static async Task<string> ErrorCode(HttpResponseMessage response)
    {
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return data.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task ExactRetryAfterCompactionAndRestartReturnsOriginalWithoutPrinterOrRenderLookup()
    {
        await using var host = await V2EndpointTests.Harness.Start();
        var body = host.PrintBody("original");
        Assert.Equal(HttpStatusCode.Accepted, (await host.Client.SendAsync(host.Request(body))).StatusCode);
        await WaitCompleted(host, "original");
        var originalId = V2ApiService.JobId("original");
        host.Clock.Now = host.Clock.Now.AddDays(8);
        Assert.Equal(1, host.Jobs.ExpireArtifacts());
        await host.RestartAsync();
        host.Inventory.Fail = true;
        host.Inventory.Items = [];
        var replay = await host.Client.SendAsync(host.Request(body));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var data = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(originalId, data.RootElement.GetProperty("id").GetString());
        Assert.Equal("completed", data.RootElement.GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, data.RootElement.GetProperty("artifactExpiredAt").ValueKind);
        Assert.Single(host.Jobs.List());
        Assert.Equal(1, host.Backend.Calls);
        Assert.Equal("IDEMPOTENCY_CONFLICT", await ErrorCode(await host.Client.SendAsync(host.Request(body.Replace("POS-1", "POS-2")))));
        Assert.Equal("ARTIFACT_EXPIRED", await ErrorCode(await host.Client.GetAsync($"/api/jobs/{originalId}/render")));
        Assert.Equal("ARTIFACT_EXPIRED", await ErrorCode(await host.Client.SendAsync(host.Reprint(originalId, "copy"))));
        using var history = JsonDocument.Parse(await host.Client.GetStringAsync("/api/jobs?station=POS-1"));
        Assert.Single(history.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(1, host.Backend.Calls);
    }

    [Fact]
    public async Task ReprintAcknowledgementReplaysAfterBothArtifactsExpireWithoutAnotherCopy()
    {
        await using var host = await V2EndpointTests.Harness.Start();
        host.Seed("original", "completed");
        Assert.Equal(HttpStatusCode.Accepted, (await host.Client.SendAsync(host.Reprint("original", "copy"))).StatusCode);
        await WaitCompleted(host, "copy");
        host.Clock.Now = host.Clock.Now.AddDays(8);
        Assert.Equal(2, host.Jobs.ExpireArtifacts());
        await host.RestartAsync();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(host.Reprint("original", "copy"))).StatusCode);
        Assert.Equal("IDEMPOTENCY_CONFLICT", await ErrorCode(await host.Client.SendAsync(host.Reprint("different-original", "copy"))));
        Assert.Equal(1, host.Backend.Calls);
        Assert.Equal(2, host.Jobs.List().Count);
    }

    [Fact]
    public async Task ConnectionAndJobsExposeConfiguredRetentionWithoutExpiringPendingReceipts()
    {
        await using var host = await V2EndpointTests.Harness.Start(new PluginSettings { AccessToken = new string('t', 32), ReceiptRetentionDays = 2 });
        using var connection = JsonDocument.Parse(await host.Client.GetStringAsync("/api/connection"));
        var policy = connection.RootElement.GetProperty("retention");
        Assert.Equal(2, policy.GetProperty("receiptDays").GetInt32());
        Assert.False(policy.GetProperty("pendingReceiptsExpire").GetBoolean());
        Assert.Equal(JobStore.MaxRetainedJobs, policy.GetProperty("maxJobs").GetInt32());
        Assert.Equal(JobStore.MaxAcceptanceBytes, policy.GetProperty("acceptanceByteBudget").GetInt64());
        var completed = host.Seed("done", "completed");
        var waiting = host.Seed("waiting", "waiting_for_printer");
        Assert.Equal(host.Clock.Now.AddDays(2), completed.ArtifactExpiresAt);
        Assert.Null(waiting.ArtifactExpiresAt);
        host.Clock.Now = host.Clock.Now.AddDays(2);
        Assert.Equal(1, host.Jobs.ExpireArtifacts());
        Assert.NotNull(host.Jobs.GetCommand("waiting")!.Prepared);
    }
}
