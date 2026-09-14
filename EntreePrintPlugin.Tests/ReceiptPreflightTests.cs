using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text;
using EntreePrintPlugin.Services;

namespace EntreePrintPlugin.Tests;

public sealed class ReceiptPreflightTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static ReceiptTextLayout Layout() => new(200, 60,
        [new("Receipt café", 0, 20, 120, 16, "Arial", false, false, "#000")], []);
    private static readonly PrinterLayoutSettings Driver = new(576, 1600, 203, 203, 640, 1700, 10, 10);

    [Fact]
    public void ComparisonFailureRejectsChangedOrUnreadableFonts()
    {
        var expected = ReceiptComparison.Create(Layout(), "driver", Driver, default, _ => "original");
        ReceiptComparison.Validate(expected, Layout(), "driver", Driver, default, _ => "original");
        foreach (var digest in new string?[] { "changed", null })
            Assert.Equal("RENDER_CHANGED", Assert.Throws<CommandException>(() =>
                ReceiptComparison.Validate(expected, Layout(), "driver", Driver, default, _ => digest)).Code);
        // Unavailable comparison during preparation does not remove ordinary printing support.
        ReceiptComparison.Validate(null, Layout(), "driver", Driver, default, _ => throw new Exception("Unexpected font read"));
    }

    [Fact]
    public void HandoffChecksFullPageSettingsAndDriverEvenWhenDpiStillMatches()
    {
        var expected = ReceiptComparison.Create(Layout(), "driver", Driver, default);
        Assert.NotNull(expected);
        Assert.Single(WindowsSpoolerTextPrinter.PreparePages(Layout(), "driver", Driver, 203, expected, default));
        PrinterLayoutSettings[] changes = [Driver with { PrintableWidthDots = 575 }, Driver with { PrintableHeightDots = 1599 },
            Driver with { PaperWidthDots = 639 }, Driver with { PaperHeightDots = 1699 },
            Driver with { OffsetXDots = 11 }, Driver with { OffsetYDots = 11 }];
        foreach (var changed in changes)
            Assert.Equal("RENDER_CHANGED", Assert.Throws<CommandException>(() =>
                WindowsSpoolerTextPrinter.PreparePages(Layout(), "driver", changed, 203, expected, default)).Code);
        Assert.Equal("RENDER_CHANGED", Assert.Throws<CommandException>(() =>
            WindowsSpoolerTextPrinter.PreparePages(Layout(), "other driver", Driver, 203, expected, default)).Code);
        Assert.Equal("PRINTER_SETTINGS_CHANGED", Assert.Throws<CommandException>(() =>
            WindowsSpoolerTextPrinter.PreparePages(Layout(), "driver", Driver with { DpiX = 300 }, 203, expected, default)).Code);
        Assert.Throws<OperationCanceledException>(() =>
            WindowsSpoolerTextPrinter.PreparePages(Layout(), "driver", Driver, 203, expected, new CancellationToken(true)));
    }

    [Theory]
    [InlineData("POS-80C")]
    [InlineData("厨房驱动 café")]
    public void NativeDriverNamePreservesUnicode(string name)
    {
        var bytes = Encoding.Unicode.GetBytes(name + '\0');
        var offset = IntPtr.Size * 16;
        var buffer = Marshal.AllocHGlobal(offset + bytes.Length);
        try
        {
            Marshal.WriteIntPtr(buffer, IntPtr.Size * 4, buffer + offset);
            Marshal.Copy(bytes, 0, buffer + offset, bytes.Length);
            Assert.Equal(name, WindowsPrinterLayout.ParseDriverName(buffer, offset + bytes.Length));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [Fact]
    public void NativeDriverNameRejectsPointersOutsideBufferAndUnterminatedNames()
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            Marshal.Copy(Enumerable.Repeat((byte)65, 256).ToArray(), 0, buffer, 256);
            foreach (var pointer in new[] { IntPtr.Zero, buffer - 2, buffer, buffer + 255, buffer + 256, buffer + 257 })
            {
                Marshal.WriteIntPtr(buffer, IntPtr.Size * 4, pointer);
                Assert.Equal("PRINTER_SETTINGS_UNAVAILABLE", Assert.Throws<CommandException>(() => WindowsPrinterLayout.ParseDriverName(buffer, 256)).Code);
            }
            Marshal.WriteIntPtr(buffer, IntPtr.Size * 4, buffer + 128);
            Assert.Throws<CommandException>(() => WindowsPrinterLayout.ParseDriverName(buffer, 256));
            Marshal.WriteInt16(buffer, 128, 0);
            Assert.Throws<CommandException>(() => WindowsPrinterLayout.ParseDriverName(buffer, 256));
            Assert.Throws<CommandException>(() => WindowsPrinterLayout.ParseDriverName(buffer, 1));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [Fact]
    public async Task WindowsPreflightReadsInstalledReceiptAndPdfDriversWithoutStartingADocument()
    {
        // Hosted runners can lack PDF queues. In that case cover the real Windows
        // missing-queue boundary; live PDF preflight evidence is recorded separately.
        var printers = PrinterSettings.InstalledPrinters.Cast<string>().Where(name => name is "Microsoft Print to PDF" or "cashier").ToArray();
        if (printers.Length == 0)
        {
            Assert.Throws<CommandException>(() => WindowsPrinterLayout.ReadDriverName("Entree-missing-" + Guid.NewGuid()));
            output.WriteLine("No pilot queue installed; exercised Windows missing-queue rejection only.");
            return;
        }
        foreach (var printer in printers)
        {
            var driver = await new WindowsPrinterLayout().ReadAsync(printer, default);
            var name = WindowsPrinterLayout.ReadDriverName(printer);
            Assert.False(string.IsNullOrWhiteSpace(name));
            var comparison = ReceiptComparison.Create(Layout(), name, driver, default);
            Assert.NotNull(comparison);
            var native = new WindowsSpoolerTextPrinter();
            native.ValidateCurrentPrinter(printer, Layout(), driver.DpiX, comparison);
            Assert.Equal("RENDER_CHANGED", Assert.Throws<CommandException>(() =>
                native.ValidateCurrentPrinter(printer, Layout(), driver.DpiX, "layout:" + new string('a', 64))).Code);
            output.WriteLine($"Read-only preflight passed: {printer}; {name}; {driver.PrintableWidthDots} dots; {driver.DpiX} DPI. No StartDoc.");
        }
    }
}
