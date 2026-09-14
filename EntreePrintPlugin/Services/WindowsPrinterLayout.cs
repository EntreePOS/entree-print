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

    internal static string ReadDriverName(string printerName)
    {
        if (!OpenPrinter(printerName, out var printer, IntPtr.Zero))
            throw new CommandException("PRINTER_SETTINGS_UNAVAILABLE", "Windows could not read the selected printer's driver.");
        try
        {
            GetPrinter(printer, 2, IntPtr.Zero, 0, out var needed);
            if (needed < IntPtr.Size * 5 || needed > 1_000_000)
                throw new CommandException("PRINTER_SETTINGS_UNAVAILABLE", "Windows returned invalid printer settings.");
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetPrinter(printer, 2, buffer, needed, out var written) || written > needed)
                    throw new CommandException("PRINTER_SETTINGS_UNAVAILABLE", "Windows printer settings could not be read consistently.");
                return ParseDriverName(buffer, (int)written);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { ClosePrinter(printer); }
    }

    internal static string ParseDriverName(IntPtr buffer, int size)
    {
        // PRINTER_INFO_2 begins with five pointers; the fifth is pDriverName.
        // Bound the pointer and terminator to the returned buffer before reading.
        if (size < IntPtr.Size * 5) throw InvalidDriverName();
        var pointer = Marshal.ReadIntPtr(buffer, IntPtr.Size * 4);
        var offset = pointer.ToInt64() - buffer.ToInt64();
        if (offset < IntPtr.Size * 5 || offset > size - 2 || offset % 2 != 0) throw InvalidDriverName();
        var available = Math.Min(1025, (size - (int)offset) / 2);
        for (var length = 0; length < available; length++)
            if (Marshal.ReadInt16(pointer, length * 2) == 0)
            {
                var name = Marshal.PtrToStringUni(pointer, length);
                return !string.IsNullOrWhiteSpace(name) ? name : throw InvalidDriverName();
            }
        throw InvalidDriverName();
    }

    private static CommandException InvalidDriverName() => new("PRINTER_SETTINGS_UNAVAILABLE", "Windows returned an invalid printer driver name.");

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenPrinter(string name, out IntPtr printer, IntPtr defaults);
    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPrinter(IntPtr printer, uint level, IntPtr buffer, uint size, out uint needed);
    [DllImport("winspool.drv")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClosePrinter(IntPtr printer);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
}
