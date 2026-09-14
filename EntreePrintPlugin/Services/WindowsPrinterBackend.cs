using EntreePrintPlugin.Models;

namespace EntreePrintPlugin.Services;

// Receipts arrive with a durable prepared layout. This layer only hands them to Windows.
public sealed class WindowsPrinterBackend(RawPrinterWriter rawPrinter, JobStore jobs,
    WindowsSpoolerMonitor spoolerMonitor) : IPrinterBackend
{
    public Task<PrintExecutionResult> ExecuteAsync(AcceptedCommand command, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            throw new PrintNotSubmittedException("Service stopped before Windows submission.");
        if (string.IsNullOrWhiteSpace(command.Printer))
            throw new CommandException("PRINTER_REQUIRED", "Select an installed Windows printer queue.");
        return Task.FromResult(command.Type switch
        {
            "print" => PrintReceipt(command),
            "beep" or "open_cash_drawer" or "cut" or "send_command" => SendRawCommand(command),
            _ => throw new CommandException("TYPE_INVALID", $"Unsupported command type: {command.Type}")
        });
    }

    private PrintExecutionResult PrintReceipt(AcceptedCommand command)
    {
        var receipt = command.Prepared ?? throw new CommandException("RENDER_REQUIRED", "Prepare a receipt before submitting it to Windows.");
        if (!command.Printer.Equals(receipt.Printer, StringComparison.OrdinalIgnoreCase))
            throw new CommandException("RENDER_PRINTER_MISMATCH", "The prepared receipt belongs to a different Windows queue.");
        WindowsTextPrinter.Validate(receipt.Layout);
        spoolerMonitor.EnsureQueue(command.Printer);
        var document = jobs.BeginSpoolerSubmission(command.Id, command.Printer);
        uint jobId;
        try
        {
            jobId = new WindowsSpoolerTextPrinter().Print(command.Printer, receipt.Layout, document,
                id => jobs.RecordSpoolerJob(command.Id, id, document), expectedDpi: receipt.Dpi);
        }
        catch (Exception error) when (error is PrintNotSubmittedException or CommandException)
        {
            // Native renderer validation/rejection raises these only before StartDoc accepts a job.
            jobs.RecordSpoolerNotSubmitted(command.Id);
            throw;
        }
        return new PrintExecutionResult("submitted", Detail: "Submitted to Windows; physical completion is not confirmed.", SpoolerJobId: jobId);
    }

    private PrintExecutionResult SendRawCommand(AcceptedCommand command)
    {
        var compact = string.Concat(command.Command.Where(c => !char.IsWhiteSpace(c)));
        if (compact.Length is < 2 or > 8192 || compact.Length % 2 != 0 || compact.Any(c => !char.IsAsciiHexDigit(c)))
            throw new CommandException("COMMAND_INVALID", "Device commands require 1–4096 complete hexadecimal bytes.");
        var bytes = Convert.FromHexString(compact);
        spoolerMonitor.EnsureQueue(command.Printer);
        var document = jobs.BeginSpoolerSubmission(command.Id, command.Printer);
        uint jobId;
        try { jobId = rawPrinter.Send(command.Printer, bytes, document, id => jobs.RecordSpoolerJob(command.Id, id, document)); }
        catch (PrintNotSubmittedException) { jobs.RecordSpoolerNotSubmitted(command.Id); throw; }
        return new PrintExecutionResult("submitted", Detail: "Device command submitted to Windows; execution is not confirmed.", SpoolerJobId: jobId);
    }
}