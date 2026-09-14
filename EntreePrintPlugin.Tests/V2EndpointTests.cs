using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntreePrintPlugin.Tests;

// The production routing/filter pipeline runs in memory. No service ports, OS queues, or hardware are opened.
public sealed class V2EndpointTests
{
    [Theory]
    [InlineData("厨房 A / 热菜")]
    [InlineData("Literal %2F and %25")]
    [InlineData("Cash & Carry + #1")]
    [InlineData("\\\\SERVER\\Queue")]
    public async Task PrinterStatus_PreservesExactQueueName(string name)
    {
        await using var host = await Harness.Start();
        host.Inventory.Items = [host.Inventory.Items[0] with { Name = name, Offline = true }];
        var response = await host.Client.GetAsync($"/api/printers/status?printer={Uri.EscapeDataString(name)}&refresh=true");
        var status = await ReadJson(response);
        Assert.True(status.GetProperty("offline").GetBoolean());
        Assert.Empty(host.Jobs.List());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?printer=")]
    [InlineData("?printer=Kitchen&printer=Other")]
    public async Task PrinterStatus_RequiresExactlyOneQueue(string query)
    {
        await using var host = await Harness.Start();
        Assert.Equal("PRINTER_REQUIRED", await Code(await host.Client.GetAsync("/api/printers/status" + query)));
    }

    [Theory]
    [InlineData("/print")]
    [InlineData("/api.js")]
    [InlineData("/sdk")]
    [InlineData("/printers")]
    [InlineData("/events")]
    [InlineData("/health")]
    [InlineData("/v2/connection")]
    public async Task PrototypeRoutes_AreNotExposed(string path)
    {
        await using var host = await Harness.Start();
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsync(path, new StringContent("{}"))).StatusCode);
        Assert.Empty(host.Jobs.List());
    }

    [Theory]
    [InlineData("https://pos.example.com", true)]
    [InlineData("https://other.example.com", false)]
    [InlineData("http://pos.example.com", false)]
    [InlineData("https://pos.example.com:8443", false)]
    public async Task Cors_PreflightAllowsOnlyConfiguredOrigin(string origin, bool allowed)
    {
        await using var host = await Harness.Start(new PluginSettings { AccessToken = EntreePrintTray.PluginConfig.GenerateAccessToken(), CorsAllowedOrigins = "https://pos.example.com" });
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/jobs");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,x-entree-service-id,x-entree-content-sha256");
        var response = await host.Client.SendAsync(request);
        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
        if (allowed)
        {
            Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Contains("authorization", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")));
        }
        Assert.Empty(host.Jobs.List());
        Assert.Equal(0, host.Backend.Calls);
    }

    [Fact]
    public async Task Cors_AllowedOriginStillRequiresToken_AndExposesSuccessfulRequestDigest()
    {
        await using var host = await Harness.Start(new PluginSettings { AccessToken = EntreePrintTray.PluginConfig.GenerateAccessToken(), CorsAllowedOrigins = "https://pos.example.com" });
        using var unauthorized = host.Request(host.PrintBody("ticket"));
        unauthorized.Headers.Add("Origin", "https://pos.example.com");
        unauthorized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "legacy-or-wrong-token");
        Assert.Equal("UNAUTHORIZED", await Code(await host.Client.SendAsync(unauthorized)));
        Assert.Empty(host.Jobs.List());
        using var authorized = host.Request(host.PrintBody("ticket"));
        authorized.Headers.Add("Origin", "https://pos.example.com");
        var response = await host.Client.SendAsync(authorized);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(V2Request.DigestHeader, string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers")));
    }

    [Fact]
    public async Task MissingConfigurationToken_FailsClosed_WhileHealthRemainsPublic()
    {
        await using var host = await Harness.Start(new PluginSettings());
        Assert.Equal("AUTH_NOT_CONFIGURED", await Code(await host.Client.GetAsync("/api/connection")));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/health")).StatusCode);
        Assert.Empty(host.Jobs.List());
    }

    [Fact]
    public async Task Render_ReturnsPreparedUnicodeHtml_WithoutSubmittingAnything()
    {
        await using var host = await Harness.Start();
        var body = JsonSerializer.Serialize(new { printer = "Kitchen",
            html = "<html><head><style>body{margin:0;font:16px Arial,'Microsoft YaHei',sans-serif}p{margin:0}</style></head><body><p>欢迎 café</p></body></html>" });
        using var request = host.Request(body);
        request.RequestUri = new Uri("/api/renders", UriKind.Relative);
        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rendered = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var html = rendered.GetProperty("html").GetString()!;
        Assert.Contains("<svg", html);
        Assert.Contains("欢迎", WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("<script", html);
        Assert.Equal(host.Identity.ServiceId, rendered.GetProperty("serviceId").GetString());
        Assert.Equal("positioned-text", rendered.GetProperty("renderer").GetString());
        Assert.Equal(host.Driver.Layout.PrintableWidthMm, rendered.GetProperty("widthMm").GetDecimal());
        Assert.Single(response.Headers.GetValues(V2Request.DigestHeader));
        Assert.Empty(host.Jobs.List());
        Assert.Equal(0, host.Backend.Calls);
    }

    [Fact]
    public async Task DriverSettingsChange_RejectsUnacceptedPreview_ButDoesNotBlockLostAckReplay()
    {
        await using var host = await Harness.Start();
        var body = host.PrintBody("first");
        var accepted = await host.Client.SendAsync(host.Request(body));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        host.Driver.Layout = host.Driver.Layout with { DpiX = 300, DpiY = 300 };
        Assert.Equal("PRINTER_SETTINGS_CHANGED", await Code(await host.Client.SendAsync(host.Request(host.PrintBody("new")))));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(host.Request(body))).StatusCode);
        Assert.Single(host.Jobs.List());
    }

    [Fact]
    public async Task Render_UsesDriverDpiForCodes_WithoutAProfileOrWidth()
    {
        await using var host = await Harness.Start();
        using var request = host.Request("""{"printer":"Kitchen","content":[{"type":"qrcode","value":"订单42"}]}""");
        request.RequestUri = new Uri("/api/renders", UriKind.Relative);
        var rendered = await ReadJson(await host.Client.SendAsync(request));
        Assert.Equal(203, rendered.GetProperty("dpi").GetInt32());
        Assert.Equal("windows_driver", rendered.GetProperty("settingsSource").GetString());
        Assert.Equal(host.Driver.Layout.PrintableWidthMm, rendered.GetProperty("widthMm").GetDecimal());
        Assert.Empty(host.Jobs.List());
        Assert.Equal(0, host.Backend.Calls);
    }

    [Fact]
    public async Task ExplicitWidth_CannotExceedDriverPrintableArea()
    {
        await using var host = await Harness.Start();
        using var request = host.Request("""{"printer":"Kitchen","widthMm":80,"html":"<p>Receipt</p>"}""");
        request.RequestUri = new Uri("/api/renders", UriKind.Relative);
        Assert.Equal("LAYOUT_OVERFLOW", await Code(await host.Client.SendAsync(request)));
        Assert.Empty(host.Jobs.List());
    }

    [Fact]
    public async Task Connection_ReturnsInventoryAndIdentity_HeartbeatDoesNotQueryPrinters()
    {
        await using var host = await Harness.Start();
        var response = await host.Client.GetAsync("/api/connection");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(host.Identity.ServiceId, json.GetProperty("serviceId").GetString());
        Assert.Single(json.GetProperty("printers").EnumerateArray());
        Assert.Equal("Kitchen", json.GetProperty("printers")[0].GetProperty("name").GetString());
        var queried = host.Inventory.Queries;
        var heartbeat = await host.Client.GetAsync("/api/heartbeat?requestId=fresh-check");
        Assert.Equal("no-store", heartbeat.Headers.CacheControl!.ToString());
        var beat = JsonDocument.Parse(await heartbeat.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("fresh-check", beat.GetProperty("requestId").GetString());
        Assert.Equal(host.Identity.BootId, beat.GetProperty("bootId").GetString());
        Assert.Equal(queried, host.Inventory.Queries);
    }

    [Theory]
    [InlineData(false, "NO_PRINTERS")]
    [InlineData(true, "PRINTER_QUERY_FAILED")]
    public async Task Connection_DistinguishesEmptyFromFailedInventory(bool failure, string code)
    {
        await using var host = await Harness.Start();
        host.Inventory.Items = [];
        host.Inventory.Fail = failure;
        var response = await host.Client.GetAsync("/api/connection");
        Assert.Equal(code, await Code(response));
    }

    [Fact]
    public async Task InvalidSubmissions_CannotCreateJobs_AndIdentityCannotBeChangedByAddress()
    {
        await using var host = await Harness.Start();
        var body = host.PrintBody("ticket");
        using var badDigest = host.Request(body);
        badDigest.Headers.Remove(V2Request.DigestHeader);
        badDigest.Headers.Add(V2Request.DigestHeader, new string('0', 64));
        Assert.Equal("CHECKSUM_INVALID", await Code(await host.Client.SendAsync(badDigest)));
        using var wrongServer = host.Request(body);
        wrongServer.Headers.Remove("X-Entree-Service-ID");
        wrongServer.Headers.Add("X-Entree-Service-ID", Guid.NewGuid().ToString());
        Assert.Equal("SERVICE_MISMATCH", await Code(await host.Client.SendAsync(wrongServer)));
        using var unauthorized = host.Request(body);
        unauthorized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        Assert.Equal("UNAUTHORIZED", await Code(await host.Client.SendAsync(unauthorized)));
        var duplicate = body[..^1] + ",\"printer\":\"Other\"}";
        Assert.Equal("REQUEST_INVALID", await Code(await host.Client.SendAsync(host.Request(duplicate))));
        Assert.Empty(host.Jobs.List());
        Assert.Equal(0, host.Backend.Calls);
    }

    [Fact]
    public async Task LostAckReplay_AfterExpiry_UsesStoredLayoutAndOriginalAcceptanceDigest()
    {
        await using var host = await Harness.Start();
        var body = host.PrintBody("ticket");
        var first = await host.Client.SendAsync(host.Request(body));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstDigest = first.Headers.GetValues(V2Request.DigestHeader).Single();
        host.Clock.Now += TimeSpan.FromHours(1);
        // New wire whitespace changes the request digest without changing the print intent.
        var replay = await host.Client.SendAsync(host.Request(body + " "));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.NotEqual(firstDigest, replay.Headers.GetValues(V2Request.DigestHeader).Single());
        var accepted = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(firstDigest, accepted.GetProperty("integrity").GetProperty("requestDigest").GetString());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (host.Backend.Calls == 0) await Task.Delay(10, deadline.Token);
        Assert.Equal(1, host.Backend.Calls);
        Assert.Equal("original receipt", host.Backend.Last!.Prepared!.Layout.Text.Single().Text);
        var id = accepted.GetProperty("id").GetString();
        var preview = await host.Client.GetAsync($"/api/jobs/{id}/render");
        Assert.Contains("original receipt", await preview.Content.ReadAsStringAsync());
        var changed = body.Replace("\"station\":\"POS-1\"", "\"station\":\"POS-2\"");
        Assert.Equal("IDEMPOTENCY_CONFLICT", await Code(await host.Client.SendAsync(host.Request(changed))));
        Assert.Equal(1, host.Backend.Calls);
    }

    [Fact]
    public async Task ExpiredPreview_ForNewIntent_IsRejectedBeforeQueueing()
    {
        await using var host = await Harness.Start();
        host.Clock.Now += TimeSpan.FromHours(1);
        Assert.Equal("RENDER_EXPIRED", await Code(await host.Client.SendAsync(host.Request(host.PrintBody("new-intent")))));
        Assert.Empty(host.Jobs.List());
        Assert.Equal(0, host.Backend.Calls);
    }

    [Fact]
    public async Task ConcurrentDuplicateRequests_HaveOneAcceptanceAndOneDelivery()
    {
        await using var host = await Harness.Start();
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => host.Client.SendAsync(host.Request(host.PrintBody("shared")))));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        Assert.All(responses, response => Assert.True(response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (host.Backend.Calls == 0) await Task.Delay(10, deadline.Token);
        Assert.Equal(1, host.Backend.Calls);
        Assert.Single(host.Jobs.List());
    }

    [Fact]
    public async Task History_FiltersBeforePaging_UsesStableTieOrderAndRetainsMetadata()
    {
        await using var host = await Harness.Start();
        var since = host.Clock.Now.ToString("O");
        for (var index = 0; index < 230; index++)
            host.Seed($"old-{index:D3}", metadata: "{\"station\":\"收银 & 1\",\"orderID\":\"42\"}");
        host.Clock.Now += TimeSpan.FromMinutes(1);
        for (var index = 0; index < 210; index++) host.Seed($"other-{index:D3}");
        var filters = $"printer=kitchen&station={Uri.EscapeDataString("收银 & 1")}&orderID=42&status=accepted&since={Uri.EscapeDataString(since)}";
        var defaultPage = await ReadJson(await host.Client.GetAsync($"/api/jobs?{filters}"));
        Assert.Equal(30, defaultPage.GetProperty("items").GetArrayLength());
        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var page = await ReadJson(await host.Client.GetAsync($"/api/jobs?{filters}&limit=100" +
                (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))));
            foreach (var job in page.GetProperty("items").EnumerateArray())
            {
                ids.Add(job.GetProperty("id").GetString()!);
                Assert.Equal("收银 & 1", job.GetProperty("metadata").GetProperty("station").GetString());
                Assert.Equal(host.Preview.Id, job.GetProperty("renderId").GetString());
                Assert.False(job.TryGetProperty("html", out _));
            }
            cursor = page.GetProperty("nextCursor").GetString();
            if (ids.Count == 100) host.Seed("new-between-pages", metadata: "{\"station\":\"收银 & 1\",\"orderID\":\"42\"}");
        } while (cursor is not null);
        Assert.Equal(Enumerable.Range(0, 230).Reverse().Select(i => $"old-{i:D3}"), ids);
        Assert.Equal(0, host.Backend.Calls);
    }

    [Theory]
    [InlineData("limit=101", "REQUEST_INVALID")]
    [InlineData("limit=1.5", "REQUEST_INVALID")]
    [InlineData("station=A&station=B", "REQUEST_INVALID")]
    [InlineData("since=2026-09-13T10:00:00", "REQUEST_INVALID")]
    [InlineData("since=2026-02-31T10:00:00Z", "REQUEST_INVALID")]
    [InlineData("status=printed", "REQUEST_INVALID")]
    [InlineData("cursor=invalid!", "CURSOR_INVALID")]
    [InlineData("offset=50", "FIELD_UNSUPPORTED")]
    public async Task History_InvalidFiltersAreRejected(string query, string code)
    {
        await using var host = await Harness.Start();
        Assert.Equal(code, await Code(await host.Client.GetAsync("/api/jobs?" + query)));
    }

    [Fact]
    public async Task History_CursorCannotChangeFiltersOrService()
    {
        await using var host = await Harness.Start();
        host.Seed("a"); host.Seed("b");
        var page = await ReadJson(await host.Client.GetAsync("/api/jobs?limit=1"));
        var cursor = Uri.EscapeDataString(page.GetProperty("nextCursor").GetString()!);
        Assert.Equal("CURSOR_INVALID", await Code(await host.Client.GetAsync($"/api/jobs?station=POS-1&cursor={cursor}")));
        await using var other = await Harness.Start();
        Assert.Equal("CURSOR_INVALID", await Code(await other.Client.GetAsync($"/api/jobs?cursor={cursor}")));
        var next = await ReadJson(await host.Client.GetAsync($"/api/jobs?limit=100&cursor={cursor}"));
        Assert.Single(next.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Reprint_ConcurrentLostAckRetriesKeepOriginalArtifactAndOneNewJob()
    {
        await using var host = await Harness.Start();
        var original = host.Seed("original", "completed");
        host.Clock.Now += TimeSpan.FromDays(1);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.Client.SendAsync(host.Reprint("original", "copy-1"))));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        Assert.All(responses, response => Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted));
        var copy = await ReadJson(responses[0]);
        var id = copy.GetProperty("id").GetString()!;
        Assert.Equal("original", copy.GetProperty("reprintOf").GetString());
        Assert.Equal(host.Preview.Id, copy.GetProperty("renderId").GetString());
        Assert.Equal("POS-1", copy.GetProperty("metadata").GetProperty("station").GetString());
        Assert.Equal(original, host.Jobs.Get("original"));
        Assert.Equal(host.Preview.ContentHash, host.Jobs.GetCommand(id)!.Prepared!.ContentHash);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (host.Backend.Calls == 0) await Task.Delay(10, deadline.Token);
        Assert.Equal(1, host.Backend.Calls);
        host.Inventory.Items = []; // A recorded replay does not depend on current hardware availability.
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(host.Reprint("original", "copy-1"))).StatusCode);
        Assert.Equal("IDEMPOTENCY_CONFLICT", await Code(await host.Client.SendAsync(host.Reprint("another", "copy-1"))));
        Assert.Equal(2, host.Jobs.List().Count);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("waiting_for_printer")]
    [InlineData("submitting")]
    [InlineData("submitted")]
    [InlineData("printing")]
    [InlineData("blocked")]
    public async Task Reprint_RejectsActiveOriginal(string state)
    {
        await using var host = await Harness.Start();
        host.Seed("original", state);
        Assert.Equal("JOB_NOT_REPRINTABLE", await Code(await host.Client.SendAsync(host.Reprint("original", "new-copy"))));
        Assert.Single(host.Jobs.List());
        Assert.Equal(0, host.Backend.Calls);
    }

    [Fact]
    public async Task Reprint_RequiresMatchingProfileAndRejectsDeviceCommands()
    {
        await using var host = await Harness.Start();
        host.Seed("original", "failed");
        host.Inventory.Items = [new() { Name = "Kitchen", DriverName = "changed" }];
        Assert.Equal("PRINTER_SETTINGS_CHANGED", await Code(await host.Client.SendAsync(host.Reprint("original", "new-copy"))));
        var command = host.Jobs.Accept(new("drawer", "open_cash_drawer", "Kitchen", "", "", "1B70", null, IdempotencyKey: "drawer"), 1).Job;
        host.Jobs.UpdateStatus(command, "completed");
        Assert.Equal("JOB_NOT_REPRINTABLE", await Code(await host.Client.SendAsync(host.Reprint("drawer", "new-copy"))));
        Assert.Equal(0, host.Backend.Calls);
    }

    [Fact]
    public async Task Reprint_AtomicallyRechecksOriginalStateAndContent()
    {
        await using var host = await Harness.Start();
        var original = host.Seed("original", "needs_attention");
        var copy = host.Jobs.ReprintSource("original") with { Id = "copy", IdempotencyKey = "copy", ReprintOf = "original" };
        host.Jobs.UpdateStatus(original, "printing");
        Assert.Equal("JOB_NOT_REPRINTABLE", Assert.Throws<CommandException>(() => host.Jobs.Accept(copy, 1)).Code);
        host.Jobs.UpdateStatus(original, "completed");
        Assert.Equal("REPRINT_CONTENT_CHANGED", Assert.Throws<CommandException>(() => host.Jobs.Accept(copy with { MetadataJson = "{}" }, 1)).Code);
        Assert.True(host.Jobs.Accept(copy, 1).Created);
        Assert.False(host.Jobs.Accept(copy, 1).Created);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<string> Code(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class Inventory : IPrinterInventory
    {
        public PrinterStatusRecord[] Items = [new() { Name = "Kitchen", DriverName = "fixture", PortName = "USB001" }];
        public int Queries;
        public bool Fail;
        public IReadOnlyList<PrinterStatusRecord> GetCachedPrinters() => Items;
        public PrinterStatusRecord? GetCachedPrinter(string name) => Items.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        public Task<IReadOnlyList<PrinterStatusRecord>> RefreshNowAsync(CancellationToken token)
        { Interlocked.Increment(ref Queries); return Fail ? Task.FromException<IReadOnlyList<PrinterStatusRecord>>(new IOException("fixture query failure")) : Task.FromResult<IReadOnlyList<PrinterStatusRecord>>(Items); }
    }

    internal sealed class Backend : IPrinterBackend
    {
        public int Calls;
        public AcceptedCommand? Last;
        public readonly System.Collections.Concurrent.ConcurrentQueue<AcceptedCommand> Executed = new();
        public Task<PrintExecutionResult> ExecuteAsync(AcceptedCommand command, CancellationToken token)
        { Last = command; Executed.Enqueue(command); Interlocked.Increment(ref Calls); return Task.FromResult(new PrintExecutionResult("completed")); }
    }

    internal sealed class Driver : IPrinterLayoutSettings
    {
        public PrinterLayoutSettings Layout = new(576, 2400, 203, 203, 640, 2400, 32, 0);
        public Task<PrinterLayoutSettings> ReadAsync(string printer, CancellationToken token) => Task.FromResult(Layout);
    }

    internal sealed class Harness : IAsyncDisposable
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "EntreeV2EndpointTests", Guid.NewGuid().ToString("N"));
        public readonly Clock Clock = new();
        public readonly Inventory Inventory = new();
        public readonly Backend Backend = new();
        public readonly Driver Driver = new();
        public ServiceIdentity Identity = null!;
        public JobStore Jobs = null!;
        public PreparedReceipt Preview = null!;
        public HttpClient Client = null!;
        private WebApplication _app = null!;
        private PrinterExecutionQueue _queue = null!;
        private PluginSettings _settings = null!;

        public static async Task<Harness> Start(PluginSettings? configuration = null)
        {
            var host = new Harness();
            host._settings = (configuration ?? new PluginSettings { AccessToken = new string('t', 32) }) with { SpoolPath = host.DirectoryPath };
            await host.Initialize();
            return host;
        }

        private async Task Initialize()
        {
            var host = this;
            host.Identity = new ServiceIdentity(host.DirectoryPath);
            var events = new EventBroadcaster();
            host.Jobs = new JobStore(events, Path.Combine(host.DirectoryPath, "jobs"), host.Clock, host._settings.ReceiptRetentionDays);
            var settings = _settings;
            var renders = new PreparedReceiptStore(Path.Combine(host.DirectoryPath, "renders"), host.Identity, host.Clock);
            var profileVersion = V2ApiService.LayoutVersion(host.Inventory.Items[0], host.Driver.Layout);
            host.Preview = renders.Save("Kitchen", 72, profileVersion, new ReceiptTextLayout(272, 60,
                [new("original receipt", 0, 20, 120, 16, "Arial", false, false, "#000")], []));
            host._queue = new PrinterExecutionQueue(NullLogger<PrinterExecutionQueue>.Instance);
            var processor = new CommandProcessor(settings, host.Jobs, host.Backend, host._queue, host.Inventory, NullLogger<CommandProcessor>.Instance);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
            builder.Services.AddSingleton(host.Identity);
            builder.Services.AddPrintCors(settings);
            builder.Services.AddSingleton(new V2ApiService(settings, host.Identity, host.Inventory, renders, host.Jobs, processor, host.Driver));
            host._app = builder.Build();
            host._app.UseCors();
            host._app.MapPrintApi(settings);
            await host._app.StartAsync();
            host.Client = host._app.GetTestClient();
            if (settings.AccessToken.Length != 0) host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
            processor.RecoverPendingJobs();
        }

        public async Task RestartAsync()
        {
            await CloseAsync();
            await Initialize();
        }

        public JobRecord Seed(string id, string state = "queued", string metadata = "{\"station\":\"POS-1\"}")
        {
            var job = Jobs.Accept(new(id, "print", "Kitchen", "", "", "", null, Preview, id, metadata, "fixture-digest"), 1).Job;
            if (state != "queued") Jobs.UpdateStatus(job, state);
            return Jobs.Get(id)!;
        }

        public HttpRequestMessage Reprint(string id, string key)
        {
            var request = Request(JsonSerializer.Serialize(new { idempotencyKey = key }));
            request.RequestUri = new Uri($"/api/jobs/{Uri.EscapeDataString(id)}/reprints", UriKind.Relative);
            return request;
        }

        public string PrintBody(string key) => JsonSerializer.Serialize(new { type = "print", printer = "Kitchen", renderId = Preview.Id,
            idempotencyKey = key, metadata = new { station = "POS-1" } });
        public HttpRequestMessage Request(string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/jobs") { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add(V2Request.DigestHeader, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            request.Headers.Add("X-Entree-Service-ID", Identity.ServiceId);
            return request;
        }

        private async Task CloseAsync()
        {
            await _queue.StopAsync(CancellationToken.None);
            Client.Dispose(); await _app.DisposeAsync(); _queue.Dispose(); Jobs.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await CloseAsync();
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
