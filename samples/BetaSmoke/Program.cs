using System.Drawing.Printing;
using System.Text.Json;
using EntreePrintPlugin.Services;
using EntreePrintPlugin.Models;
using Microsoft.Extensions.Logging;
using System.Drawing;
using ZXing;
using ZXing.Common;

// This smoke test submits ONLY to the named Microsoft PDF virtual printer, never a thermal/default printer.
if (args is ["--verify-codes", var imagePath])
{
    // Read-only decoder for a rasterized PDF page. Does not touch any print queue.
    using var bitmap = new Bitmap(imagePath);
    var pixels = new byte[checked(bitmap.Width * bitmap.Height * 3)];
    for (var y = 0; y < bitmap.Height; y++)
    for (var x = 0; x < bitmap.Width; x++)
    {
        var color = bitmap.GetPixel(x, y);
        var index = (y * bitmap.Width + x) * 3;
        pixels[index] = color.R; pixels[index + 1] = color.G; pixels[index + 2] = color.B;
    }
    var source = new RGBLuminanceSource(pixels, bitmap.Width, bitmap.Height, RGBLuminanceSource.BitmapFormat.RGB24);
    var values = new[] { BarcodeFormat.QR_CODE, BarcodeFormat.CODE_128 }.Select(format =>
        new BarcodeReaderGeneric { Options = new DecodingOptions { TryHarder = true, PossibleFormats = [format] } }
            .Decode(source)?.Text).Where(value => value is not null).ToArray();
    if (!values.Contains("欢迎 https://entree.example/10086") || !values.Contains("ORDER10086"))
        throw new Exception("The PDF page's QR and barcode did not both decode: " + JsonSerializer.Serialize(values));
    Console.WriteLine(JsonSerializer.Serialize(new { ok = true, physicalPrinting = false, decoded = values }));
    return;
}
if (args.Length is < 1 or > 2 || args.Length == 2 && args[1] != "--long")
    throw new ArgumentException("Provide a fresh output directory, optionally followed by --long.");
var longReceipt = args.Length == 2;
var directory = Path.GetFullPath(args[0]);
Directory.CreateDirectory(directory);
const string html = """
<!doctype html><html><head><style>
body { margin:0; padding:8px; font:16px Arial, 'Microsoft YaHei', sans-serif; }
h1 { font-size:22px; margin:0 0 12px; } p { margin:8px 0; }
.rule { border-top:1px solid black; margin:12px 0; }
</style></head><body><h1>ENTREE Print</h1><p>欢迎 · 歡迎 · café</p>
<div class="rule"></div><p>Ticket 0001 — Kitchen</p><p>Fried rice × 2</p><p>Total: $17.98</p></body></html>
""";
const string printer = "Microsoft Print to PDF";
var driver = await new WindowsPrinterLayout().ReadAsync(printer, CancellationToken.None);
if (driver.DpiX != driver.DpiY) throw new Exception("The smoke fixture needs uniform driver DPI.");
var codes = VectorCodeRenderer.ToHtml(VectorCodeRenderer.Encode("qrcode", "欢迎 https://entree.example/10086", dpi: driver.DpiX), "")
    + "<p>Order barcode</p>" + VectorCodeRenderer.ToHtml(VectorCodeRenderer.Encode("code128", "ORDER10086", availableWidthMm: 68, dpi: driver.DpiX), "ORDER10086", showText: true);
var items = longReceipt ? string.Concat(Enumerable.Range(1, 120).Select(i => $"<p>Item {i:D3} - Fried rice</p>")) : "";
var layout = await ReceiptLayoutEngine.PrepareAsync(html.Replace("</body>", items + codes + "</body>"), 72, directory, CancellationToken.None);
var pages = ReceiptPaginator.Paginate(layout, driver.PrintableWidthDots * 96f / driver.DpiX, driver.PrintableHeightDots * 96f / driver.DpiY);
if (longReceipt && pages.Count < 2) throw new Exception("Long receipt did not exercise multiple pages.");
for (var index = 0; index < pages.Count; index++)
    await File.WriteAllTextAsync(Path.Combine(directory, $"page-{index + 1}.svg"), WindowsTextPrinter.ToSvg(pages[index]));
if (!layout.Text.Any(run => run.Text.Contains("欢迎"))) throw new Exception("Unicode text was lost.");
if (layout.Rectangles.Length < 100) throw new Exception("QR/barcode vector geometry was lost.");
var preview = WindowsTextPrinter.ToSvg(layout);
await File.WriteAllTextAsync(Path.Combine(directory, "preview.html"), preview);
await File.WriteAllTextAsync(Path.Combine(directory, "layout.json"), JsonSerializer.Serialize(layout));
WindowsTextPrinter.WriteMetafile(Path.Combine(directory, "receipt.emf"), layout);
if (!PrinterSettings.InstalledPrinters.Cast<string>().Contains(printer)) throw new Exception("Microsoft Print to PDF is required for the virtual spooler smoke test.");
var pdf = Path.Combine(directory, "receipt.pdf");
if (File.Exists(pdf)) throw new Exception("Choose a fresh output directory; the smoke test does not overwrite existing PDFs.");
uint createdId = 0;
using var logs = LoggerFactory.Create(builder => builder.AddConsole());
using var jobs = new JobStore(new EventBroadcaster(), Path.Combine(directory, "jobs"));
using var monitor = new WindowsSpoolerMonitor(jobs, logs.CreateLogger<WindowsSpoolerMonitor>());
await monitor.StartAsync(CancellationToken.None);
monitor.EnsureQueue(printer);
var (job, _) = jobs.Accept(new AcceptedCommand("virtual-smoke", "print", printer, html, "", "", null), 1);
var document = jobs.BeginSpoolerSubmission(job.Id, printer);
var id = new WindowsSpoolerTextPrinter().Print(printer, layout, document, value =>
{
    createdId = value;
    jobs.RecordSpoolerJob(job.Id, value, document);
}, pdf, expectedDpi: driver.DpiX);
jobs.UpdateStatus(job, "submitted");
if (id == 0 || id != createdId) throw new Exception("Windows job identity was not captured.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
while (!File.Exists(pdf) || new FileInfo(pdf).Length == 0) await Task.Delay(100, timeout.Token);
var bytes = await File.ReadAllBytesAsync(pdf);
if (!bytes.AsSpan(0, Math.Min(5, bytes.Length)).SequenceEqual("%PDF-"u8)) throw new Exception("Virtual printer did not produce PDF output.");
while (jobs.Get(job.Id)!.Status is not ("completed" or "needs_attention")) await Task.Delay(100, timeout.Token);
var tracked = jobs.Get(job.Id)!;
if (tracked.Status == "completed" && (tracked.WindowsStatus.GetValueOrDefault() & 0x1080) == 0)
    throw new Exception("Completion was reported without positive Windows job evidence.");
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await monitor.StopAsync(stop.Token);
var result = new { ok = true, physicalPrinting = false, printer, spoolerJobId = id, tracked.Status, tracked.SpoolerState,
    tracked.WindowsStatus, tracked.SpoolerEvidence, tracked.WindowsDocumentName, tracked.SpoolerHandoffCompletedAt,
    textRuns = layout.Text.Length, rectangles = layout.Rectangles.Length, pdfBytes = bytes.Length,
    expectedPages = pages.Count, expectedItems = longReceipt ? 120 : 0, driverDpi = driver.DpiX };
await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result));
Console.WriteLine(JsonSerializer.Serialize(result));
