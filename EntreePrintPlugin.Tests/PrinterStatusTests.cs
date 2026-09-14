using System.Text.Json;
using EntreePrintPlugin.Services;

namespace EntreePrintPlugin.Tests;

public sealed class PrinterStatusTests
{
    [Fact]
    public async Task WindowsQueryOutput_PreservesUnicodeQueueNamesAcrossTheProcessBoundary()
    {
        // Exercise the real hidden PowerShell/stdout boundary with a read-only fixture.
        var json = await PrinterStatusService.RunPowerShellAsync("""
            [pscustomobject]@{
                printers = @([pscustomobject]@{ Name = '厨房 · 收銀 · café'; PrinterStatus = 0; PortName = 'IP_192.0.2.10' })
                cim = @([pscustomobject]@{ Name = '厨房 · 收銀 · café'; Default = $true; DetectedErrorState = 0 })
                ports = @([pscustomobject]@{ Name = 'IP_192.0.2.10'; PrinterHostAddress = '192.0.2.10'; PortNumber = 9100 })
            } | ConvertTo-Json -Compress -Depth 5
            """, CancellationToken.None);
        var printer = Assert.Single(PrinterStatusService.ParseStatusPayload(json));
        Assert.Equal("厨房 · 收銀 · café", printer.Name);
        Assert.Equal("192.0.2.10", printer.HostAddress);
        Assert.Equal(9100, printer.PortNumber);
        Assert.Equal("windows", printer.StatusSource);
        Assert.True(printer.Default);
        Assert.Null(printer.PaperOut);
        Assert.Null(printer.CoverOpen);
        Assert.Null(printer.DrawerOpen);
    }

    [Fact]
    public async Task WindowsQueryFailure_IsNotAnEmptySuccessfulInventory()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PrinterStatusService.RunPowerShellAsync("[Console]::Error.WriteLine('查询失败'); exit 1", CancellationToken.None));
        Assert.Contains("查询失败", error.Message);
    }

    [Theory]
    [InlineData(3, "paper_low", false, false)]
    [InlineData(4, "paper_out", true, false)]
    [InlineData(5, "ready", false, false)]
    [InlineData(6, "error", false, false)]
    [InlineData(7, "cover_open", false, true)]
    [InlineData(9, "offline", false, false)]
    public void CimErrors_DoNotConfusePaperTonerAndCover(int error, string status, bool paperOut, bool coverOpen)
    {
        var json = JsonSerializer.Serialize(new
        {
            printers = new[] { new { Name = "Kitchen", PrinterStatus = 0, WorkOffline = false } },
            cim = new[] { new { Name = "Kitchen", PrinterStatus = 3, DetectedErrorState = error } }
        });
        var actual = PrinterStatusService.ParseStatusPayload(json).Single();
        Assert.Equal(status, actual.Status);
        Assert.Equal(paperOut, actual.PaperOut == true);
        Assert.Equal(coverOpen, actual.CoverOpen == true);
        if (error == 9) Assert.True(actual.Offline);
        if (error == 3) Assert.True(actual.PaperLow);
    }

    [Theory]
    [InlineData(3, "paused")]
    [InlineData(16, "paper_out")]
    [InlineData(128, "offline")]
    [InlineData(4194304, "cover_open")]
    public void NativeFlags_AreNotInterpretedAsCimIdle(int flags, string status)
    {
        var json = JsonSerializer.Serialize(new { printers = new[] { new { Name = "Kitchen", PrinterStatus = flags } } });
        Assert.Equal(status, PrinterStatusService.ParseStatusPayload(json).Single().Status);
    }

    [Fact]
    public void CimOnlyInventory_UsesCimEnum_AndKeepsUnreportedSensorsUnknown()
    {
        var actual = PrinterStatusService.ParseStatusPayload("""
            {"cim":[{"Name":"USB receipt","PrinterStatus":3,"DetectedErrorState":0}]}
            """).Single();
        Assert.Equal("ready", actual.Status); // Windows reports idle; it has not confirmed physical sensor state.
        Assert.Null(actual.PaperOut);
        Assert.Null(actual.Offline);
        Assert.Null(actual.CoverOpen);
        Assert.Null(actual.PaperLow);
    }
}
