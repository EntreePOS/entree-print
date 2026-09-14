using System.ComponentModel;
using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;

namespace EntreePrintPlugin.Services;

// GDI submission preserves D's drawing code while exposing the actual Windows job ID.
public sealed class WindowsSpoolerTextPrinter
{
    public uint Print(string printerName, ReceiptTextLayout layout, string documentName,
        Action<uint>? onJobCreated = null, string? outputFile = null, int? expectedDpi = null)
    {
        WindowsTextPrinter.Validate(layout);
        // The preview froze font family choices. Do not silently substitute a different family on replay.
        foreach (var family in layout.Text.Select(run => run.Family).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var font = new Font(family, 12);
            if (!font.FontFamily.Name.Equals(family, StringComparison.OrdinalIgnoreCase))
                throw new CommandException("FONT_UNAVAILABLE", $"The prepared receipt's font '{family}' is no longer available.");
        }
        var printer = new PrinterSettings { PrinterName = printerName, Copies = 1 };
        if (!printer.IsValid) throw new CommandException("PRINTER_NOT_FOUND", "Select an installed Windows printer.");
        var page = printer.DefaultPageSettings;
        // Preserve Windows paper, orientation, resolution and vendor-specific driver options.
        var mode = printer.GetHdevmode(page);
        var modePointer = GlobalLock(mode);
        if (modePointer == IntPtr.Zero) { GlobalFree(mode); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        IntPtr dc;
        try { dc = CreateDC(null, printerName, null, modePointer); }
        finally { GlobalUnlock(mode); GlobalFree(mode); }
        if (dc == IntPtr.Zero) throw new PrintNotSubmittedException("Windows could not open a printer drawing context.");
        var documentStarted = false;
        try
        {
            var dpiX = GetDeviceCaps(dc, 88);
            var dpiY = GetDeviceCaps(dc, 90);
            if (expectedDpi.HasValue && (dpiX != expectedDpi || dpiY != expectedDpi))
                throw new CommandException("PRINTER_SETTINGS_CHANGED", "Windows printer resolution changed after this receipt was prepared.");
            if (dpiX <= 0 || dpiY <= 0)
                throw new CommandException("PRINTER_SETTINGS_UNAVAILABLE", "The Windows driver did not report a usable resolution.");
            // Plan every page before StartDoc: invalid/oversize sections cannot leave partial output.
            var pages = ReceiptPaginator.Paginate(layout, GetDeviceCaps(dc, 8) * 96f / dpiX, GetDeviceCaps(dc, 10) * 96f / dpiY, dpiY);
            var info = new DocInfo { Size = Marshal.SizeOf<DocInfo>(), Name = documentName, Output = outputFile };
            var id = StartDoc(dc, info);
            if (id <= 0) throw new PrintNotSubmittedException("Windows rejected the print document before submission.");
            documentStarted = true;
            onJobCreated?.Invoke((uint)id);
            foreach (var receiptPage in pages)
            {
                Check(StartPage(dc), "start the page");
                using (var graphics = Graphics.FromHdc(dc))
                {
                    graphics.PageUnit = GraphicsUnit.Inch;
                    graphics.PageScale = 1;
                    graphics.ScaleTransform(1f / 96, 1f / 96);
                    WindowsTextPrinter.Draw(graphics, receiptPage);
                    graphics.Flush();
                }
                Check(EndPage(dc), "finish the page");
            }
            Check(EndDoc(dc), "finish the document");
            documentStarted = false;
            return (uint)id;
        }
        finally
        {
            if (documentStarted) AbortDoc(dc);
            DeleteDC(dc);
        }
    }

    private static void Check(int value, string step)
    {
        if (value <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Windows could not {step}; output may be partial.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocInfo
    {
        public int Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name = "";
        [MarshalAs(UnmanagedType.LPWStr)] public string? Output;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DataType;
        public int Flags;
    }
    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDC(string? driver, string device, string? port, IntPtr mode);
    [DllImport("gdi32.dll", EntryPoint = "StartDocW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDoc(IntPtr dc, [In] DocInfo info);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int StartPage(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int EndPage(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int EndDoc(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int AbortDoc(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);
}
