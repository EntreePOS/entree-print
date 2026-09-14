using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;

namespace EntreePrintPlugin.Tests;

public sealed class JobKeyLookupTests
{
    private static string Url(string key) => "/api/jobs/lookup?idempotencyKey=" + Uri.EscapeDataString(key);
    private static JobRecord Seed(V2EndpointTests.Harness host, string key, string state)
    {
        var command = new AcceptedCommand(V2ApiService.JobId(key), "print", "Kitchen", "", "", "", null,
            host.Preview, key, "{\"station\":\"POS-1\"}", new string('a', 64));
        var job = host.Jobs.Accept(command, 1).Job;
        host.Jobs.UpdateStatus(job, state);
        return job;
    }

    [Theory]
    [InlineData("订单 / 收银 + #1 & %2F")]
    [InlineData("leading-and-trailing ")]
    [InlineData("  leading-space")]
    [InlineData("CaseSensitive")]
    public async Task LookupPreservesExactKeyAndDoesNotQueryOrPrint(string key)
    {
        await using var host = await V2EndpointTests.Harness.Start();
        var job = Seed(host, key, "waiting_for_printer");
        host.Inventory.Fail = true;
        host.Inventory.Items = [];
        using var response = await host.Client.GetAsync(Url(key));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(job.Id, result.RootElement.GetProperty("id").GetString());
        Assert.Equal(key, result.RootElement.GetProperty("idempotencyKey").GetString());
        Assert.Equal("waiting_for_printer", result.RootElement.GetProperty("state").GetString());
        Assert.Equal(0, host.Backend.Calls);
        Assert.Equal(0, host.Inventory.Queries);
        Assert.Single(host.Jobs.List());
    }

    [Fact]
    public async Task LookupSurvivesArtifactCompactionAndRestart()
    {
        await using var host = await V2EndpointTests.Harness.Start();
        var job = Seed(host, "persisted-pos-intent", "completed");
        host.Clock.Now = host.Clock.Now.AddDays(8);
        Assert.Equal(1, host.Jobs.ExpireArtifacts());
        await host.RestartAsync();
        using var result = JsonDocument.Parse(await host.Client.GetStringAsync(Url("persisted-pos-intent")));
        Assert.Equal(job.Id, result.RootElement.GetProperty("id").GetString());
        Assert.Equal("completed", result.RootElement.GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, result.RootElement.GetProperty("artifactExpiredAt").ValueKind);
        Assert.Equal(0, host.Backend.Calls);
        Assert.Null(host.Jobs.GetCommand(job.Id)!.Prepared);
    }

    [Fact]
    public async Task UnknownOrDifferentlyCasedKeyReturnsNotFoundWithoutCreatingWork()
    {
        await using var host = await V2EndpointTests.Harness.Start();
        Seed(host, "CaseSensitive", "completed");
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(Url("casesensitive"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(Url("unknown"))).StatusCode);
        Assert.Single(host.Jobs.List());
        Assert.Equal(0, host.Backend.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?idempotencyKey=")]
    [InlineData("?idempotencyKey=%20%20")]
    [InlineData("?idempotencyKey=one&idempotencyKey=two")]
    [InlineData("?idempotencyKey=one&unexpected=two")]
    public async Task InvalidLookupIsRejectedBeforeLedgerOrPrinterChanges(string query)
    {
        await using var host = await V2EndpointTests.Harness.Start();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await host.Client.GetAsync("/api/jobs/lookup" + query)).StatusCode);
        Assert.Empty(host.Jobs.List());
        Assert.Equal(0, host.Backend.Calls);
    }

    [Fact]
    public async Task LookupRequiresAuthorizationAndMatchingServiceIdentityAndBoundsKeyLength()
    {
        await using var host = await V2EndpointTests.Harness.Start();
        Seed(host, "key", "completed");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await host.Client.GetAsync(Url(new string('x', 201)))).StatusCode);
        host.Client.DefaultRequestHeaders.Add("X-Entree-Service-ID", "another-service");
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.GetAsync(Url("key"))).StatusCode);
        host.Client.DefaultRequestHeaders.Remove("X-Entree-Service-ID");
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(Url("key"))).StatusCode);
        Assert.Single(host.Jobs.List());
        Assert.Equal(0, host.Backend.Calls);
    }
}
