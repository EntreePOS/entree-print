using System.Drawing.Printing;
using System.Runtime.InteropServices;

namespace EntreePrintPlugin.Services;

public sealed record PrinterLayoutSettings(int PrintableWidthDots, int PrintableHeightDots, int DpiX, int DpiY,
    int PaperWidthDots, int PaperHeightDots, int OffsetXDots, int OffsetYDots)
{
    public decimal PrintableWidthMm => PrintableWidthDots * 25.4m / DpiX;
}

public interface IPrinterLayoutSettings
{
    Task<PrinterLayoutSettings> ReadAsync(string printer, CancellationToken token);
}

// Measurement graphics reads the driver's current defaults without starting a print job.
public sealed class WindowsPrinterLayout : IPrinterLayoutSettings
{
    private readonly SemaphoreSlim _slots = new(2);
    public async Task<PrinterLayoutSettings> ReadAsync(string printer, CancellationToken token)
    {
        if (!await _slots.WaitAsync(0, token)) throw new CommandException("PRINTER_SETTINGS_UNAVAILABLE", "Windows printer settings are busy. Try again shortly.");
        var query = Task.Run(() =>
        {
            try { return Read(printer); }
            finally { _slots.Release(); }
        });
        try { return await query.WaitAsync(TimeSpan.FromSeconds(8), token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not CommandException)
        { throw new CommandException("PRINTER_SETTINGS_UNAVAILABLE", "Windows could not read this printer's settings. Check the installed driver and Printing Preferences."); }
    }

    private static PrinterLayoutSettings Read(string name)
    {
        var printer = new PrinterSettings { PrinterName = name };
        if (!printer.IsValid) throw new CommandException("PRINTER_NOT_FOUND", "Select an installed Windows printer queue.");
        using var graphics = printer.CreateMeasurementGraphics(printer.DefaultPageSettings);
        var dc = graphics.GetHdc();
        try
        {
            var result = new PrinterLayoutSettings(GetDeviceCaps(dc, 8), GetDeviceCaps(dc, 10), GetDeviceCaps(dc, 88), GetDeviceCaps(dc, 90),
                GetDeviceCaps(dc, 110), GetDeviceCaps(dc, 111), GetDeviceCaps(dc, 112), GetDeviceCaps(dc, 113));
            if (result.PrintableWidthDots <= 0 || result.PrintableHeightDots <= 0 || result.DpiX <= 0 || result.DpiY <= 0)
                throw new CommandException("PRINTER_SETTINGS_UNAVAILABLE", "The Windows driver did not report a usable printable area or resolution.");
            return result;
        }
        finally { graphics.ReleaseHdc(dc); }
    }

    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
}
