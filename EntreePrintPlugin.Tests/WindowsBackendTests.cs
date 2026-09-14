using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntreePrintPlugin.Tests;

// All cases reject before Windows queue access. No physical or virtual jobs are submitted.
public sealed class WindowsBackendTests : IDisposable
{
    private readonly JobStore _jobs = new(new EventBroadcaster());
    private readonly WindowsSpoolerMonitor _monitor;
    private readonly WindowsPrinterBackend _backend;
    public WindowsBackendTests()
    {
        _monitor = new(_jobs, NullLogger<WindowsSpoolerMonitor>.Instance);
        _backend = new(new RawPrinterWriter(), _jobs, _monitor);
    }
    private static AcceptedCommand Command(string type = "print", string queue = "Fixture only", string hex = "") =>
        new("intent", type, queue, "<p>Receipt</p>", "", hex, null);

    [Fact]
    public async Task UnpreparedReceipt_CannotFallBackToRenderingDuringDelivery()
    {
        var error = await Assert.ThrowsAsync<CommandException>(() => _backend.ExecuteAsync(Command(), CancellationToken.None));
        Assert.Equal("RENDER_REQUIRED", error.Code);
        Assert.Empty(_jobs.List());
    }

    [Theory]
    [InlineData("beep")]
    [InlineData("open_cash_drawer")]
    [InlineData("cut")]
    [InlineData("send_command")]
    public async Task MissingDeviceBytes_DoNotUseGlobalDefaultCommands(string type)
    {
        var error = await Assert.ThrowsAsync<CommandException>(() => _backend.ExecuteAsync(Command(type), CancellationToken.None));
        Assert.Equal("COMMAND_INVALID", error.Code);
        Assert.Empty(_jobs.List());
    }

    [Fact]
    public async Task UnspecifiedQueue_CannotFallBackToTheWindowsDefaultPrinter()
    {
        var error = await Assert.ThrowsAsync<CommandException>(() => _backend.ExecuteAsync(Command(queue: ""), CancellationToken.None));
        Assert.Equal("PRINTER_REQUIRED", error.Code);
    }

    [Fact]
    public async Task ShutdownBeforeDelivery_IsExplicitlyNotSubmitted()
    {
        await Assert.ThrowsAsync<PrintNotSubmittedException>(() => _backend.ExecuteAsync(Command(), new CancellationToken(true)));
        Assert.Empty(_jobs.List());
    }

    [Fact]
    public async Task PreparedReceiptCannotChangeDestinationAtHandoff()
    {
        var receipt = new PreparedReceipt("fixture", "service", "Another queue", 72, "settings", "fixture",
            new(272, 50, [], []), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30));
        var error = await Assert.ThrowsAsync<CommandException>(() => _backend.ExecuteAsync(Command() with { Prepared = receipt }, CancellationToken.None));
        Assert.Equal("RENDER_PRINTER_MISMATCH", error.Code);
        Assert.Empty(_jobs.List());
    }
    public void Dispose() { _monitor.Dispose(); _jobs.Dispose(); }
}
