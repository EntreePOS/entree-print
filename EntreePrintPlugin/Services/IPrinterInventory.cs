using EntreePrintPlugin.Models;

namespace EntreePrintPlugin.Services;

public interface IPrinterInventory
{
    IReadOnlyList<PrinterStatusRecord> GetCachedPrinters();
    PrinterStatusRecord? GetCachedPrinter(string name);
    Task<IReadOnlyList<PrinterStatusRecord>> RefreshNowAsync(CancellationToken token);
}
