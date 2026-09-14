using System.Collections.Concurrent;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntreePrintPlugin.Tests;

public sealed class OfflineQueueTests
{
    [Theory]
    [InlineData("offline")]
    [InlineData("paper_out")]
    [InlineData("cover_open")]
    [InlineData("paused")]
    public async Task BlockedPrinter_RetainsReceiptsAcrossRestart_WithoutReprintingCompletedDestination(string reason)
    {
        var directory = Path.Combine(Path.GetTempPath(), "EntreeOfflineQueueTests", Guid.NewGuid().ToString("N"));
        var backend = new Backend();
        try
        {
            AcceptedCommand kitchen;
            AcceptedCommand next;
            AcceptedCommand cashier;
            await using (var first = new QueueHost(directory, backend))
            {
                first.Inventory.Block(reason);
                kitchen = first.Receipt("kitchen-1", "Kitchen");
                next = first.Receipt("kitchen-2", "Kitchen");
                cashier = first.Receipt("cashier", "Cashier");
                first.Processor.AcceptValidated(kitchen);
                first.Processor.AcceptValidated(next);
                first.Processor.AcceptValidated(cashier);
                // The configured retry limit is one; several device polls must not exhaust it.
                await Until(() => first.Inventory.KitchenReads >= 3 && first.Jobs.Get("cashier")?.Status == "completed");
                Assert.Equal("waiting_for_printer", first.Jobs.Get("kitchen-1")!.Status);
                Assert.Equal(reason, first.Jobs.Get("kitchen-1")!.RetryReason);
                Assert.Equal(0, first.Jobs.Get("kitchen-1")!.Attempts);
                Assert.Equal("queued", first.Jobs.Get("kitchen-2")!.Status);
                Assert.Equal(new[] { "cashier" }, backend.Executed.ToArray());
            }
            await using (var second = new QueueHost(directory, backend))
            {
                Assert.Equal("欢迎 · kitchen-1", second.Jobs.GetCommand("kitchen-1")!.Prepared!.Layout.Text.Single().Text);
                Assert.Equal(2, second.Jobs.RecoverableJobs().Count);
                Assert.False(second.Processor.AcceptValidated(cashier).Created);
                second.Processor.RecoverPendingJobs();
                second.Processor.RecoverPendingJobs();
                await Until(() => second.Jobs.Get("kitchen-2")?.Status == "completed");
                Assert.Equal(new[] { "cashier", "kitchen-1", "kitchen-2" }, backend.Executed.ToArray());
                Assert.False(second.Processor.AcceptValidated(kitchen).Created);
                Assert.False(second.Processor.AcceptValidated(next).Created);
                Assert.Equal(1, second.Jobs.Get("kitchen-1")!.Attempts);
                Assert.Equal(1, second.Jobs.Get("kitchen-2")!.Attempts);
                Assert.Empty(second.Jobs.RecoverableJobs());
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task PrinterReturns_ContinuesTheExistingQueue_WithoutRepeatedStorageWrites()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EntreeOfflineQueueTests", Guid.NewGuid().ToString("N"));
        var backend = new Backend();
        try
        {
            await using var host = new QueueHost(directory, backend);
            host.Inventory.Block("offline");
            host.Processor.AcceptValidated(host.Receipt("receipt", "Kitchen"));
            await Until(() => host.Jobs.Get("receipt")?.Status == "waiting_for_printer");
            var original = host.Jobs.Get("receipt")!;
            await Until(() => host.Inventory.KitchenReads >= 4);
            Assert.Equal(original.Version, host.Jobs.Get("receipt")!.Version);
            Assert.Empty(backend.Executed);
            host.Inventory.Block("");
            await Until(() => host.Jobs.Get("receipt")?.Status == "completed");
            Assert.Equal(new[] { "receipt" }, backend.Executed.ToArray());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task StaleOfflineObservation_DoesNotPreventWindowsFromHandlingTheJob()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EntreeOfflineQueueTests", Guid.NewGuid().ToString("N"));
        var backend = new Backend();
        try
        {
            await using var host = new QueueHost(directory, backend);
            host.Inventory.Block("offline", stale: true);
            host.Processor.AcceptValidated(host.Receipt("receipt", "Kitchen"));
            await Until(() => host.Jobs.Get("receipt")?.Status == "completed");
            Assert.Equal(new[] { "receipt" }, backend.Executed.ToArray());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Inventory : IPrinterInventory
    {
        private volatile PrinterStatusRecord _kitchen = new() { Name = "Kitchen" };
        public int KitchenReads;
        public void Block(string reason, bool stale = false) => _kitchen = new()
        {
            Name = "Kitchen", Stale = stale, Offline = reason == "offline", PaperOut = reason == "paper_out",
            CoverOpen = reason == "cover_open", Paused = reason == "paused"
        };
        public IReadOnlyList<PrinterStatusRecord> GetCachedPrinters() => [_kitchen, new() { Name = "Cashier" }];
        public PrinterStatusRecord? GetCachedPrinter(string name)
        {
            if (name == "Kitchen") { Interlocked.Increment(ref KitchenReads); return _kitchen; }
            return new() { Name = name };
        }
        public Task<IReadOnlyList<PrinterStatusRecord>> RefreshNowAsync(CancellationToken token) => Task.FromResult(GetCachedPrinters());
    }

    private sealed class Backend : IPrinterBackend
    {
        public readonly ConcurrentQueue<string> Executed = new();
        public Task<PrintExecutionResult> ExecuteAsync(AcceptedCommand command, CancellationToken token)
        {
            Executed.Enqueue(command.Id);
            return Task.FromResult(new PrintExecutionResult("completed"));
        }
    }

    private sealed class QueueHost : IAsyncDisposable
    {
        public readonly Inventory Inventory = new();
        public readonly JobStore Jobs;
        public readonly CommandProcessor Processor;
        private readonly PrinterExecutionQueue _queue = new(NullLogger<PrinterExecutionQueue>.Instance);
        private readonly PreparedReceiptStore _renders;
        public QueueHost(string directory, IPrinterBackend backend)
        {
            var events = new EventBroadcaster();
            Jobs = new JobStore(events, Path.Combine(directory, "jobs"));
            _renders = new PreparedReceiptStore(Path.Combine(directory, "renders"), new ServiceIdentity(directory));
            var settings = new PluginSettings { PrintRetryMaxAttempts = 1, PrintRetryDelayMs = 250 };
            Processor = new CommandProcessor(settings, Jobs, backend, _queue, Inventory, NullLogger<CommandProcessor>.Instance);
        }
        public AcceptedCommand Receipt(string id, string printer)
        {
            var render = _renders.Save(printer, 72, "driver-fixture", new(272, 60,
                [new("欢迎 · " + id, 0, 20, 160, 16, "Arial", false, false, "#000")], []), 203);
            return new(id, "print", printer, "", "", "", null, render, id, "{\"station\":\"tablet-1\"}");
        }
        public async ValueTask DisposeAsync()
        {
            await _queue.StopAsync(CancellationToken.None);
            _queue.Dispose(); Jobs.Dispose();
        }
    }
}
