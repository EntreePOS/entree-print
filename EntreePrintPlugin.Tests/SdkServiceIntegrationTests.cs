using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;

namespace EntreePrintPlugin.Tests;

public sealed class SdkServiceIntegrationTests
{
    [Theory]
    [InlineData("lost-ack", 2)]
    [InlineData("offline", 2)]
    [InlineData("integrity", 1)]
    [InlineData("retention", 1)]
    [InlineData("key-lookup", 1)]
    [InlineData("outbox", 1)]
    [InlineData("sqlite-outbox", 1)]
    [InlineData("events", 0)]
    [InlineData("after", 3)]
    public async Task ActualSdk_UsesServicePipelineAndDurableLedger(string scenario, int expectedDeliveries)
    {
        await using var host = await V2EndpointTests.Harness.Start(new PluginSettings
        {
            AccessToken = new string('t', 32), PrintRetryDelayMs = 250,
            PrinterProfiles = new() { ["厨房 A / 热菜"] = new() { CutMode = "raw", CutCommandHex = "1D5600", BeepCommandHex = "1B420101" } }
        });
        host.Inventory.Items = [host.Inventory.Items[0], new PrinterStatusRecord
        {
            Name = "厨房 A / 热菜", DriverName = "fixture", PortName = "IP_fixture", Offline = false
        }];
        host.Client.DefaultRequestHeaders.Clear(); // Only the SDK supplies credentials and integrity headers.
        var start = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "sdk", "integration", "service-contract.mjs"));
        start.ArgumentList.Add(scenario);
        using var node = Process.Start(start)!;
        var errors = node.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var finished = false;
        try
        {
            while (await node.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                using var message = JsonDocument.Parse(line);
                var input = message.RootElement;
                if (input.TryGetProperty("done", out _)) { finished = true; break; }
                var id = input.GetProperty("id").GetInt32();
                object reply;
                if (input.TryGetProperty("control", out var control))
                    reply = await Control(host, input, id, control.GetString()!, deadline.Token);
                else
                {
                    var url = new Uri(input.GetProperty("url").GetString()!);
                    Assert.Equal("127.0.0.1", url.Host);
                    using var request = new HttpRequestMessage(new HttpMethod(input.GetProperty("method").GetString()!), url.PathAndQuery);
                    if (input.GetProperty("bodyBase64").ValueKind == JsonValueKind.String)
                        request.Content = new ByteArrayContent(Convert.FromBase64String(input.GetProperty("bodyBase64").GetString()!));
                    foreach (var header in input.GetProperty("headers").EnumerateObject())
                        if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString()))
                            request.Content!.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
                    if (scenario == "events" && url.AbsolutePath == "/api/events" && host.Jobs.Get("event-fixture") is null)
                        host.Seed("event-fixture", "completed", "{\"orderID\":\"订单42\"}");
                    using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    var responseBytes = scenario == "events" && response.IsSuccessStatusCode && url.AbsolutePath == "/api/events"
                        ? await ReadThroughCheckpoint(response, deadline.Token)
                        : await response.Content.ReadAsByteArrayAsync(deadline.Token);
                    reply = new
                    {
                        id, status = (int)response.StatusCode,
                        headers = response.Headers.Concat(response.Content.Headers).ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value)),
                        bodyBase64 = Convert.ToBase64String(responseBytes)
                    };
                }
                await node.StandardInput.WriteLineAsync(JsonSerializer.Serialize(reply).AsMemory(), deadline.Token);
                await node.StandardInput.FlushAsync(deadline.Token);
            }
            await node.WaitForExitAsync(deadline.Token);
            var error = await errors;
            Assert.True(node.ExitCode == 0 && finished, $"Node SDK scenario {scenario} failed: {error}{Environment.NewLine}{string.Join(Environment.NewLine, host.Errors)}");
            Assert.Equal(expectedDeliveries, host.Backend.Calls);
            Assert.Equal(scenario is "events" or "after" ? 1 : expectedDeliveries, host.Jobs.List().Count);
            Assert.All(host.Backend.Executed, command =>
            {
                if (command.Type != "print")
                {
                    Assert.Equal("after",scenario);
                    Assert.Null(command.Prepared);
                    Assert.Equal(command.Type == "cut" ? "1D5600" : "1B420101",command.Command);
                    return;
                }
                Assert.NotNull(command.Prepared);
                Assert.Contains("欢迎", string.Join(" ", command.Prepared.Layout.Text.Select(text => text.Text)));
                Assert.NotNull(command.RequestDigest);
            });
        }
        finally
        {
            if (!node.HasExited) { node.Kill(entireProcessTree: true); await node.WaitForExitAsync(); }
        }
    }

    // Forward the actual SSE bytes through the existing stdio bridge, ending
    // after catch-up to exercise SDK resume on EOF. No socket listener is used.
    private static async Task<byte[]> ReadThroughCheckpoint(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        while (bytes.Length < 2_000_000)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0) throw new IOException("Event stream ended before its checkpoint.");
            bytes.Write(buffer, 0, count);
            var text = Encoding.UTF8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
            if (text.Contains("event: caught-up") && (text.EndsWith("\n\n") || text.EndsWith("\r\n\r\n"))) return bytes.ToArray();
        }
        throw new IOException("Event fixture exceeded its bounded response.");
    }

    private static async Task<object> Control(V2EndpointTests.Harness host, JsonElement input, int id, string command, CancellationToken token)
    {
        switch (command)
        {
            case "waitState":
                var jobId = V2ApiService.JobId(input.GetProperty("key").GetString()!);
                var state = input.GetProperty("state").GetString();
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    while (host.Jobs.Get(jobId) is not { } job || JobStore.DisplayState(job) != state) await Task.Delay(10, timeout.Token);
                }
                break;
            case "offline":
                var printer = input.GetProperty("printer").GetString();
                host.Inventory.Items = host.Inventory.Items.Select(item => item.Name == printer
                    ? item with { Offline = input.GetProperty("offline").GetBoolean() } : item).ToArray();
                break;
            case "restart":
                if (input.TryGetProperty("expirePreviews", out var expire) && expire.GetBoolean()) host.Clock.Now += TimeSpan.FromHours(1);
                await host.RestartAsync();
                host.Client.DefaultRequestHeaders.Clear();
                return new { id, serviceId = host.Identity.ServiceId, bootId = host.Identity.BootId };
            case "expireReceipts":
                host.Clock.Now += TimeSpan.FromDays(8);
                return new { id, expired = host.Jobs.ExpireArtifacts() };
            case "updateEventFixture":
                host.Jobs.UpdateStatus(host.Jobs.Get("event-fixture")!, "completed", "Recovered observation");
                return new { id, cursor = EventStream.Cursor(host.Identity.ServiceId, host.Events.History!.LatestCursor) };
            case "saveClientIntent":
                using (var file = new FileStream(Path.Combine(host.DirectoryPath, "client-intent.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, input.GetProperty("intent"));
                    file.Flush(flushToDisk: true);
                }
                break;
            case "readClientIntent":
                return new { id, intent = JsonSerializer.Deserialize<JsonElement>(File.ReadAllBytes(Path.Combine(host.DirectoryPath, "client-intent.json"))) };
            case "inspect":
                return new
                {
                    id,
                    jobs = host.Jobs.List().Select(job => new { id = job.Id, key = host.Jobs.GetCommand(job.Id)!.IdempotencyKey, state = job.Status, attempts = job.Attempts }),
                    deliveries = host.Backend.Executed.Select(item => new { key = item.IdempotencyKey, printer = item.Printer })
                };
            default: throw new InvalidOperationException($"Unknown fixture command {command}");
        }
        return new { id };
    }
}
