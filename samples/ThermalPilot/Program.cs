using System.Security.Cryptography;
using System.Text.Json;
using EntreePrintPlugin;
using EntreePrintPlugin.Services;
using Microsoft.Extensions.Logging;

// Explicit physical pilot. A claimed output directory cannot be reused to print twice.
if (args is not ["--print-cashier", var output])
    throw new ArgumentException("Use --print-cashier <fresh-output-directory> only when cashier is ready for one physical test receipt.");
const string printer = "cashier";
var directory = Path.GetFullPath(output);
Directory.CreateDirectory(directory);
using (var claim = new FileStream(Path.Combine(directory, "pilot.claim"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
{ claim.Write("one physical test only"u8); claim.Flush(true); }
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var settings = new PluginSettings { SpoolPath = directory, PrintRetryMaxAttempts = 1 };
var events = new EventBroadcaster();
using var logs = LoggerFactory.Create(builder => builder.AddConsole());
using var inventory = new PrinterStatusService(settings, events, logs.CreateLogger<PrinterStatusService>());
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
await inventory.RefreshNowAsync(deadline.Token);
var selected = inventory.GetCachedPrinter(printer) ?? throw new Exception("cashier queue is not installed.");
if (selected.Offline == true || selected.PaperOut == true || selected.Paused == true)
    throw new Exception("Windows reports cashier is not ready. No print submitted.");
var driverSettings = new WindowsPrinterLayout();
var driver = await driverSettings.ReadAsync(printer, deadline.Token);
using var jobs = new JobStore(events, Path.Combine(directory, "jobs"));
var identity = new ServiceIdentity(directory);
var renders = new PreparedReceiptStore(Path.Combine(directory, "renders"), identity);
using var monitor = new WindowsSpoolerMonitor(jobs, logs.CreateLogger<WindowsSpoolerMonitor>());
using var queue = new PrinterExecutionQueue(logs.CreateLogger<PrinterExecutionQueue>());
var backend = new WindowsPrinterBackend(new RawPrinterWriter(), jobs, monitor);
var processor = new CommandProcessor(settings, jobs, backend, queue, inventory, logs.CreateLogger<CommandProcessor>());
var api = new V2ApiService(settings, identity, inventory, renders, jobs, processor, driverSettings);
V2Request Request(object body)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(body, json);
    return V2Request.Parse(bytes, Convert.ToHexString(SHA256.HashData(bytes)));
}
const string html = """
<style>body{margin:0;padding:4px;font:16px Arial,'Microsoft YaHei',sans-serif}p{margin:5px 0}h1{font-size:20px;margin:0 0 6px}</style>
<h1>ENTREE BETA TEST</h1><p>测试小票 · 測試小票 · café</p>
<p>Cashier / 收银台</p><p>炒饭 Fried rice × 2 — $17.98</p>
<p>Text clarity / 清晰度测试</p>
""";
var rendered = JsonSerializer.SerializeToElement(await api.RenderAsync(Request(new
{
    printer, content = new object[] {
        new { type = "html", html },
        new { type = "qrcode", value = "ENTREE-BETA-001", sizeMm = 24 },
        new { type = "barcode", format = "code128", value = "BETA001", heightMm = 10, showText = true }
    }
}), deadline.Token), json);
await File.WriteAllTextAsync(Path.Combine(directory, "preview.html"), rendered.GetProperty("html").GetString());
await File.WriteAllTextAsync(Path.Combine(directory, "render.json"), rendered.GetRawText());
var renderId = rendered.GetProperty("id").GetString()!;
var prepared = renders.Get(renderId);
var pages = ReceiptPaginator.Paginate(prepared.Layout, driver.PrintableWidthDots * 96f / driver.DpiX, driver.PrintableHeightDots * 96f / driver.DpiY);
if (pages.Count != 1) throw new Exception("Pilot must fit one driver page. Nothing submitted.");
var key = "thermal-pilot-" + Guid.NewGuid().ToString("N");
await File.WriteAllTextAsync(Path.Combine(directory, "intent.json"), JsonSerializer.Serialize(new { printer, key, renderId, driver }, json));
await monitor.StartAsync(deadline.Token);
await queue.StartAsync(deadline.Token);
try
{
    var accepted = await api.SubmitAsync(Request(new { type = "print", printer, renderId, idempotencyKey = key,
        metadata = new { title = "ENTREE BETA TEST", source = "thermal-pilot" } }), deadline.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { accepted.Created, accepted.Job }, json));
    var jobId = V2ApiService.JobId(key);
    var stopAt = DateTimeOffset.UtcNow.AddSeconds(25);
    while (DateTimeOffset.UtcNow < stopAt && jobs.Get(jobId)!.Status is not ("completed" or "failed" or "needs_attention"))
        await Task.Delay(200, deadline.Token);
    var tracked = jobs.Get(jobId)!;
    var result = new { physicalTestSubmitted = tracked.SpoolerJobId.HasValue, physicalQualityConfirmed = false,
        printer, driver, widthMm = prepared.WidthMm, heightMm = rendered.GetProperty("heightMm"), pages = pages.Count,
        job = api.GetJob(jobId), qr = "ENTREE-BETA-001", barcode = "BETA001" };
    var resultJson = JsonSerializer.Serialize(result, json);
    await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), resultJson);
    Console.WriteLine(resultJson);
}
finally
{
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await queue.StopAsync(stop.Token);
    await monitor.StopAsync(stop.Token);
}
