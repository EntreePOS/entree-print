using System.Text.Json;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;

// One controlled partial job on the explicitly named virtual queue only. The
// callback aborts before drawing/EndDoc; no thermal queue or service is started.
const string printer = "Microsoft Print to PDF";
var directory = Path.Combine(Path.GetTempPath(), "EntreeSpoolerIdentityAudit", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var ledger = Path.Combine(directory, "jobs");
var output = Path.Combine(directory, "aborted.pdf");
using var initial = new JobStore(new EventBroadcaster(), ledger);
initial.Accept(new AcceptedCommand("fixture", "print", printer, "", "", "", null, IdempotencyKey: "fixture"), 1);
var document = initial.BeginSpoolerSubmission("fixture", printer);
var layout = new ReceiptTextLayout(200, 60, [new("Identity recovery fixture", 0, 20, 180, 12, "Arial", false, false, "#000")], []);
uint? createdId = null;
JobRecord? recoveredJob = null;
var before = WindowsSpoolerMonitor.ReadQueueJobs(printer).Count;
try
{
    new WindowsSpoolerTextPrinter().Print(printer, layout, document, id =>
    {
        createdId = id;
        initial.Dispose(); // Simulate ledger reopen before the job-ID callback was saved.
        using var recovered = new JobStore(new EventBroadcaster(), ledger);
        if (recovered.Get("fixture")!.SpoolerJobId is not null) throw new InvalidOperationException("Fixture unexpectedly retained the Windows ID.");
        var snapshot = WindowsSpoolerMonitor.ReadQueueJobs(printer);
        WindowsSpoolerMonitor.ReconcileUnidentifiedJobs(recovered, printer, snapshot);
        recoveredJob = recovered.Get("fixture")!;
        if (recoveredJob.SpoolerJobId != id || recoveredJob.WindowsDocumentName != document
            || !recoveredJob.SpoolerHandoffUncertain || recoveredJob.SpoolerHandoffCompletedAt is not null
            || recoveredJob.Status != "needs_attention" || recoveredJob.SpoolerState is not ("submitted" or "printing" or "blocked")
            || recovered.RecoverableJobs().Count != 0)
            throw new InvalidOperationException("The active Windows job was not safely reconciled.");
        try { recovered.ReprintSource("fixture"); throw new InvalidOperationException("An active partial job was reprintable."); }
        catch (CommandException error) when (error.Code == "JOB_NOT_REPRINTABLE") { }
        throw new StopBeforeDrawingException();
    }, outputFile: output);
    throw new InvalidOperationException("The fixture unexpectedly finished a document.");
}
catch (StopBeforeDrawingException) { }
finally
{
    // WindowsSpoolerTextPrinter's finally calls AbortDoc for the fixture. Verify
    // removal by both ID and document identity; never modify unrelated jobs.
    if (createdId is uint id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (WindowsSpoolerMonitor.ReadQueueJobs(printer).Any(job => job.Id == id && job.Document == document))
            await Task.Delay(100, deadline.Token);
    }
}
var result = new
{
    printer, directory, priorQueueJobs = before, windowsJobId = createdId,
    matchedPersistedDocument = recoveredJob?.WindowsDocumentName == document,
    state = recoveredJob?.Status, spoolerState = recoveredJob?.SpoolerState,
    handoffUncertain = recoveredJob?.SpoolerHandoffUncertain,
    evidence = recoveredJob?.SpoolerEvidence,
    fixtureRemovedAfterAbort = true, completedReceipt = false,
    pdfFileExists = File.Exists(output)
};
var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(directory, "result.json"), json);
Console.WriteLine(json);

internal sealed class StopBeforeDrawingException : Exception;
