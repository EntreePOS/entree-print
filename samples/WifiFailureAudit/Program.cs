using System.Text.Json;
using EntreePrintPlugin;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;
using Microsoft.Extensions.Logging.Abstractions;

// No Windows print writer, browser, network connection or status background worker.
// Historical pre-fix observations remain in observed-results.json; this checks current code.
var observations = new List<object>();
var ledgerRoot = Path.Combine(Path.GetTempPath(), "EntreeWifiAudit", Guid.NewGuid().ToString("N"));
try
{
    var backend = new CountingBackend();
    using (var first = new Harness(backend, Path.Combine(ledgerRoot, "replay")))
    {
        await first.Accept("lost-ack", "<p>Original 中文</p>");
        await Wait(() => first.Jobs.Get("lost-ack")?.Status == "completed");
        await first.Accept("lost-ack", "<p>Original 中文</p>");
        Require(backend.Calls == 1, "lost ACK must not resubmit");
        try { await first.Accept("lost-ack", "<p>Changed</p>"); throw new Exception("Conflict was accepted"); }
        catch (CommandException error) when (error.Code == "IDEMPOTENCY_CONFLICT") { }
    }
    using (var restarted = new Harness(backend, Path.Combine(ledgerRoot, "replay")))
    {
        await restarted.Accept("lost-ack", "<p>Original 中文</p>");
        Require(backend.Calls == 1, "restart replay must use durable identity");
    }
    observations.Add(new { scenario = "lost_ack_restart_and_payload_conflict", submissions = backend.Calls, result = "one output; altered content rejected" });

    var pendingPath = Path.Combine(ledgerRoot, "pending");
    using (var before = new JobStore(new EventBroadcaster(), pendingPath))
    {
        before.Accept(new AcceptedCommand("pending", "print", "fake-only", "<p>Pending</p>", "", "", null), 2);
    }
    using (var after = new JobStore(new EventBroadcaster(), pendingPath))
    {
        Require(after.RecoverableJobs().Single().Command.Id == "pending", "unsent intent must survive restart");
    }
    observations.Add(new { scenario = "accepted_unsent_restart", recovered = true });

    var uncertain = new CountingBackend(() => throw new IOException("network disconnected after simulated acceptance"));
    using (var harness = new Harness(uncertain, Path.Combine(ledgerRoot, "uncertain")))
    {
        await harness.Accept("uncertain", "<p>May already have printed</p>");
        await Wait(() => harness.Jobs.Get("uncertain")?.Status == "needs_attention");
        await harness.Accept("uncertain", "<p>May already have printed</p>");
        Require(uncertain.Calls == 1, "uncertain handoff must not retry");
    }
    observations.Add(new { scenario = "network_failure_after_possible_handoff", submissions = uncertain.Calls, finalStatus = "needs_attention" });
    var json = JsonSerializer.Serialize(new { characterization = false, physicalPrinting = false, observations }, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (args.Length == 1) await File.WriteAllTextAsync(Path.GetFullPath(args[0]), json);
}
finally { if (Directory.Exists(ledgerRoot)) Directory.Delete(ledgerRoot, true); }

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task Wait(Func<bool> predicate)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    while (!predicate()) await Task.Delay(10, deadline.Token);
}

sealed class Harness : IDisposable
{
    private readonly CommandProcessor processor;
    public JobStore Jobs { get; }
    public Harness(IPrinterBackend backend, string path)
    {
        var settings = new PluginSettings { PrintRetryMaxAttempts = 2, PrintRetryDelayMs = 250 };
        var events = new EventBroadcaster();
        Jobs = new JobStore(events, path);
        var status = new PrinterStatusService(settings, events, NullLogger<PrinterStatusService>.Instance);
        processor = new CommandProcessor(settings, Jobs, backend, new PrinterExecutionQueue(NullLogger<PrinterExecutionQueue>.Instance), status, NullLogger<CommandProcessor>.Instance);
    }
    public Task Accept(string id, string html)
    {
        processor.AcceptValidated(new AcceptedCommand(id, "print", "fake-only", html, "", "", null));
        return Task.CompletedTask;
    }
    public void Dispose() => Jobs.Dispose();
}

sealed class CountingBackend(Func<Task>? onSubmit = null) : IPrinterBackend
{
    private int calls;
    public int Calls => Volatile.Read(ref calls);
    public async Task<PrintExecutionResult> ExecuteAsync(AcceptedCommand command, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        if (onSubmit is not null) await onSubmit();
        return new PrintExecutionResult("completed", Detail: "Fake only");
    }
}
