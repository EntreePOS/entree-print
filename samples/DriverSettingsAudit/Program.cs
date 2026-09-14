using System.Text.Json;
using EntreePrintPlugin.Services;
using EntreePrintPlugin;
using Microsoft.Extensions.Logging.Abstractions;

// Read-only driver inspection. No StartDoc, queue mutation, receipt or device commands.
if (args is ["--inventory"])
{
    using var inventory = new PrinterStatusService(new PluginSettings(), new EventBroadcaster(), NullLogger<PrinterStatusService>.Instance);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var printers = await inventory.RefreshNowAsync(timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { count = printers.Count, printers }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
var printer = args.Length == 1 ? args[0] : "Microsoft Print to PDF";
var settings = await new WindowsPrinterLayout().ReadAsync(printer, CancellationToken.None);
Console.WriteLine(JsonSerializer.Serialize(new { printer, settings }, new JsonSerializerOptions { WriteIndented = true }));
