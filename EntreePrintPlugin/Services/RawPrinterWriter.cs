using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EntreePrintPlugin.Services;

public sealed class RawPrinterWriter
{
    public uint Send(string printerName, byte[] bytes, string documentName, Action<uint>? onJobCreated = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Raw printer writes require Windows.");
        }

        if (string.IsNullOrWhiteSpace(printerName))
        {
            throw new ArgumentException("Printer name is required.", nameof(printerName));
        }

        if (!OpenPrinter(printerName, out var printerHandle, IntPtr.Zero))
        {
            throw new PrintNotSubmittedException($"Windows could not open printer: {printerName} (error {Marshal.GetLastWin32Error()}).");
        }

        try
        {
            var docInfo = new DocInfo
            {
                pDocName = documentName,
                DataType = "RAW"
            };

            var jobId = StartDocPrinter(printerHandle, 1, docInfo);
            if (jobId == 0)
            {
                throw new PrintNotSubmittedException($"Windows rejected the raw document before submission (error {Marshal.GetLastWin32Error()}).");
            }

            try
            {
                onJobCreated?.Invoke(jobId);
                if (!StartPagePrinter(printerHandle))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to start raw printer page.");
                }

                if (!WritePrinter(printerHandle, bytes, bytes.Length, out var written) || written != bytes.Length)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to write all raw printer bytes.");
                }
                if (!EndPagePrinter(printerHandle))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to finish raw printer page.");
                if (!EndDocPrinter(printerHandle))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to finish raw printer document.");
                return jobId;
            }
            catch
            {
                // Abort rather than finalize a known incomplete document. Partial output remains possible.
                AbortPrinter(printerHandle);
                throw;
            }
        }
        finally
        {
            ClosePrinter(printerHandle);
        }
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string printerName, out IntPtr printerHandle, IntPtr printerDefaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint StartDocPrinter(IntPtr printerHandle, int level, [In] DocInfo docInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool AbortPrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr printerHandle, byte[] data, int count, out int written);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pDocName = "";

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pOutputFile;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string DataType = "RAW";
    }
}
