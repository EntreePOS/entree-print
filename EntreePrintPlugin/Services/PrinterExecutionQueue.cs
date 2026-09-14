using System.Collections.Concurrent;

namespace EntreePrintPlugin.Services;

public sealed class PrinterExecutionQueue(ILogger<PrinterExecutionQueue> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;
    private readonly ConcurrentDictionary<string, SerialQueue> _queues = new(StringComparer.OrdinalIgnoreCase);

    public void Enqueue(string printerName, Func<CancellationToken, Task> worker)
    {
        if (_shutdown.IsCancellationRequested) throw new CommandException("SERVICE_STOPPING", "Service is stopping; the durable intent will be reconciled on restart.");
        var key = string.IsNullOrWhiteSpace(printerName) ? "<default>" : printerName.Trim();
        var queue = _queues.GetOrAdd(key, _ => new SerialQueue(logger, key, _shutdown.Token));
        queue.Enqueue(worker);
    }

    public Task StartAsync(CancellationToken token) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken token)
    {
        _shutdown.Cancel();
        await Task.WhenAll(_queues.Values.Select(queue => queue.Completion)).WaitAsync(token);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel(); _shutdown.Dispose();
    }

    private sealed class SerialQueue(ILogger logger, string key, CancellationToken stoppingToken)
    {
        private readonly Queue<Func<CancellationToken, Task>> _items = new();
        private readonly object _gate = new();
        private bool _running;
        public Task Completion { get; private set; } = Task.CompletedTask;

        public void Enqueue(Func<CancellationToken, Task> worker)
        {
            lock (_gate)
            {
                _items.Enqueue(worker);
                if (_running)
                {
                    return;
                }
                _running = true;
                Completion = Task.Run(RunAsync);
            }
        }

        private async Task RunAsync()
        {
            while (true)
            {
                Func<CancellationToken, Task> worker;
                lock (_gate)
                {
                    if (_items.Count == 0 || stoppingToken.IsCancellationRequested)
                    {
                        _running = false;
                        return;
                    }
                    worker = _items.Dequeue();
                }

                try
                {
                    await worker(stoppingToken);
                }
                catch (Exception error)
                {
                    logger.LogWarning(error, "Printer queue {Printer} worker failed.", key);
                }
            }
        }
    }
}
