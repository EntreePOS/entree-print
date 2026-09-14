using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;

// Exercises actual process termination against the production ledger. Never opens or writes a printer.
static AcceptedCommand Command(string html = "<p>中文 receipt</p>") => new("kill-test", "print", "fake-only", html, "", "", null);

if (args is ["--child", var childDirectory, var stage])
{
    using var childJobs = new JobStore(new EventBroadcaster(), Path.Combine(childDirectory, "jobs"));
    var (job, _) = childJobs.Accept(Command(), 2);
    if (stage != "accepted")
    {
        var document = childJobs.BeginSpoolerSubmission(job.Id, "fake-only");
        if (stage != "attempt") childJobs.RecordSpoolerJob(job.Id, 42, document);
        if (stage is "submitted" or "completed") childJobs.UpdateStatus(job, "submitted");
        if (stage == "completed") childJobs.ApplySpoolerObservation(job.Id, 42, document, 0x80, "fixture");
    }
    await File.WriteAllTextAsync(Path.Combine(childDirectory, "ready"), Environment.ProcessId.ToString());
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

if (args.Length != 1) throw new ArgumentException("Provide a fresh output directory.");
var directory = Path.GetFullPath(args[0]);
if (Directory.Exists(directory)) throw new ArgumentException("Use a fresh directory to preserve earlier evidence.");
Directory.CreateDirectory(directory);
var results = new List<object>();
var processPath = Environment.ProcessPath ?? throw new Exception("Current executable path is unavailable.");
foreach (var checkpoint in new[] { "accepted", "attempt", "submitting", "submitted", "completed" })
{
    var caseDirectory = Path.Combine(directory, checkpoint);
    Directory.CreateDirectory(caseDirectory);
    var start = new ProcessStartInfo(processPath)
    {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardError = true, RedirectStandardOutput = true
    };
    if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--child"); start.ArgumentList.Add(caseDirectory); start.ArgumentList.Add(checkpoint);
    using var child = Process.Start(start) ?? throw new Exception("Child did not start.");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    try
    {
        while (!File.Exists(Path.Combine(caseDirectory, "ready")))
        {
            if (child.HasExited) throw new Exception(await child.StandardError.ReadToEndAsync());
            await Task.Delay(25, deadline.Token);
        }
        var readyPid = await File.ReadAllTextAsync(Path.Combine(caseDirectory, "ready"), deadline.Token);
        if (readyPid != child.Id.ToString()) throw new Exception("Ready marker belongs to a different child.");
        child.Kill(entireProcessTree: true); // Only the process handle created above; no service or printer process is touched.
        await child.WaitForExitAsync(deadline.Token);
        using var recovered = new JobStore(new EventBroadcaster(), Path.Combine(caseDirectory, "jobs"));
        var record = recovered.Get("kill-test") ?? throw new Exception("Accepted job was lost.");
        var expectedStatus = checkpoint switch { "accepted" => "queued", "submitted" => "submitted", "completed" => "completed", _ => "needs_attention" };
        if (record.Status != expectedStatus) throw new Exception($"{checkpoint}: expected {expectedStatus}, got {record.Status}.");
        if (recovered.RecoverableJobs().Count != (checkpoint == "accepted" ? 1 : 0))
            throw new Exception("An uncertain or sent job was automatically requeued.");
        if (recovered.Accept(Command(), 2).Created) throw new Exception("Replayed intent created a duplicate after process termination.");
        try { recovered.Accept(Command("changed"), 2); throw new Exception("Changed payload was accepted."); }
        catch (CommandException error) when (error.Code == "IDEMPOTENCY_CONFLICT") { }
        results.Add(new { checkpoint, passed = true, recovered = record.Status, record.SpoolerJobId, requeued = recovered.RecoverableJobs().Count });
    }
    finally
    {
        if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
    }
}
var report = new { ok = true, physicalPrinting = false, results };
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), json);
Console.WriteLine(json);
