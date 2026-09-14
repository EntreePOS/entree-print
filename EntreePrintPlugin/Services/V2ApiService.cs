using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntreePrintPlugin.Models;

namespace EntreePrintPlugin.Services;

public sealed class V2ApiService(PluginSettings settings, ServiceIdentity identity, IPrinterInventory printers,
    PreparedReceiptStore renders, JobStore jobs, CommandProcessor processor, IPrinterLayoutSettings driverSettings)
{
    private readonly SemaphoreSlim _renderSlots = new(2);

    public object Health(string requestId = "") => new
    {
        service = "entree-print-plugin", identity.ServiceId, identity.BootId, version = ServiceIdentity.Version,
        apiVersion = "0.0.1", rendererVersion = ServiceIdentity.Version, requestId, serverTime = DateTimeOffset.UtcNow,
        readiness = new { service = "ready", printerInventory = printers.GetCachedPrinters().Any(p => !p.Stale) ? "observed" : "unknown" }
    };

    public async Task<object> ConnectionAsync(CancellationToken token, string ip = "")
    {
        var inventory = await InventoryAsync(true, token);
        if (inventory.Length == 0) throw new CommandException("NO_PRINTERS", "This service has no installed Windows printers.");
        return new { identity.ServiceId, identity.BootId, apiVersion = "0.0.1", rendererVersion = ServiceIdentity.Version,
            ip, port = settings.HttpPort, capabilities = new { render = true, print = true, qrcode = true, barcode = new[] { "code39", "code128" }, images = false },
            retention = new { receiptDays = settings.ReceiptRetentionDays, pendingReceiptsExpire = false,
                idempotency = "retained", maxJobs = JobStore.MaxRetainedJobs, maxPreparedJobs = JobStore.MaxPreparedJobs,
                acceptanceByteBudget = JobStore.MaxAcceptanceBytes }, printers = inventory };
    }

    public async Task<object[]> InventoryAsync(bool refresh, CancellationToken token)
    {
        IReadOnlyList<PrinterStatusRecord> inventory;
        try { inventory = refresh ? await printers.RefreshNowAsync(token) : printers.GetCachedPrinters(); }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        { throw new CommandException("PRINTER_QUERY_FAILED", "Windows printer inventory could not be refreshed."); }
        return inventory.Select(PrinterView).ToArray();
    }

    private async Task<PrinterStatusRecord> FindPrinterAsync(string name, CancellationToken token)
    {
        var printer = printers.GetCachedPrinter(name);
        if (printer is null || printer.Stale)
        {
            await InventoryAsync(true, token);
            printer = printers.GetCachedPrinter(name);
        }
        return printer ?? throw new CommandException("PRINTER_NOT_FOUND", "Select an installed Windows printer queue.");
    }

    public async Task<object> StatusAsync(string name, bool refresh, CancellationToken token)
    {
        if (refresh) await InventoryAsync(true, token);
        return StatusView(await FindPrinterAsync(name, token));
    }

    private PrinterProfile? Profile(string name) => settings.PrinterProfiles.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    internal static string LayoutVersion(PrinterStatusRecord printer, PrinterLayoutSettings layout) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { printer.Name, printer.DriverName, layout }))).ToLowerInvariant();

    private object PrinterView(PrinterStatusRecord printer) => PrinterView(settings, printer);
    internal object[] CachedInventory() => printers.GetCachedPrinters().Select(PrinterView).ToArray();

    internal static object PrinterView(PluginSettings settings, PrinterStatusRecord printer)
    {
        return new
        {
            name = printer.Name, isDefault = printer.Default,
            connection = new { type = printer.PortName.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ? "usb" : !string.IsNullOrEmpty(printer.HostAddress) ? "network" : "unknown", host = printer.HostAddress },
            settings = new { source = "windows_driver" },
            status = StatusView(settings, printer)
        };
    }

    private object StatusView(PrinterStatusRecord printer) => StatusView(settings, printer);

    private static object StatusView(PluginSettings settings, PrinterStatusRecord printer)
    {
        var profile = settings.PrinterProfiles.FirstOrDefault(pair => pair.Key.Equals(printer.Name, StringComparison.OrdinalIgnoreCase)).Value;
        return new { state = printer.Stale ? "unknown" : printer.Status, stale = printer.Stale, observedAt = printer.UpdatedAt,
                source = printer.StatusSource, offline = printer.Offline, paperOut = printer.PaperOut, paperLow = printer.PaperLow,
                coverOpen = printer.CoverOpen, paused = printer.Paused,
                capabilities = new { beep = profile?.BeepCommandHex is not null, openDrawer = profile?.DrawerCommandHex is not null,
                    cut = profile?.CutCommandHex is not null && profile.CutMode != "driver", sendCommand = profile?.AllowRawCommands == true }
        };
    }

    public async Task<object> RenderAsync(V2Request request, CancellationToken token)
    {
        var body = request.Body;
        V2Request.Fields(body, "printer", "html", "content", "widthMm");
        var printer = await FindPrinterAsync(V2Request.String(body, "printer"), token);
        var layoutSettings = await driverSettings.ReadAsync(printer.Name, token);
        var width = V2Request.Number(body, "widthMm") ?? layoutSettings.PrintableWidthMm;
        if (width < 20 || width > 100) throw new CommandException("LAYOUT_INVALID", "Printable width must be between 20 and 100 mm.");
        if (width > layoutSettings.PrintableWidthMm)
            throw new CommandException("LAYOUT_OVERFLOW", "Requested content width exceeds the Windows driver's printable area. Omit widthMm to use the driver width.");
        if (body.TryGetProperty("html", out _) == body.TryGetProperty("content", out var content))
            throw new CommandException("CONTENT_INVALID", "Supply exactly one of html or content.");
        var html = body.TryGetProperty("html", out _) ? V2Request.String(body, "html", 250000) : BuildBlocks(content, width,
            layoutSettings.DpiX == layoutSettings.DpiY ? layoutSettings.DpiX : null);
        if (!await _renderSlots.WaitAsync(0, token)) throw new CommandException("RENDER_BUSY", "The renderer is busy; retry preparation shortly.");
        var work = Path.Combine(settings.SpoolPath, "render-work", Guid.NewGuid().ToString("N"));
        try
        {
            var layout = await ReceiptLayoutEngine.PrepareAsync(html, width, work, token, settings.BrowserExecutablePath);
            return PreparedReceiptStore.View(renders.Save(printer.Name, width, LayoutVersion(printer, layoutSettings), layout,
                layoutSettings.DpiX == layoutSettings.DpiY ? layoutSettings.DpiX : null));
        }
        finally
        {
            _renderSlots.Release();
            if (Directory.Exists(work)) try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    internal static string BuildBlocks(JsonElement blocks, decimal width, int? dpi)
    {
        if (blocks.ValueKind != JsonValueKind.Array || blocks.GetArrayLength() is < 1 or > 200)
            throw new CommandException("CONTENT_INVALID", "Content must contain 1–200 blocks.");
        var html = new StringBuilder();
        foreach (var block in blocks.EnumerateArray())
        {
            var type = V2Request.String(block, "type");
            string fragment;
            if (type == "html")
            {
                V2Request.Fields(block, "type", "html");
                fragment = V2Request.String(block, "html", 250000);
            }
            else if (type is "qrcode" or "barcode")
            {
                if (dpi is null) throw new CommandException("PRINTER_SETTINGS_UNSUPPORTED", "This driver does not report matching horizontal and vertical resolution required by the current code renderer.");
                if (type == "qrcode") V2Request.Fields(block, "type", "value", "sizeMm", "align");
                else V2Request.Fields(block, "type", "format", "value", "heightMm", "showText", "align");
                var value = V2Request.String(block, "value", 1024);
                var format = type == "qrcode" ? type : V2Request.String(block, "format");
                if (type == "barcode" && format is not ("code39" or "code128"))
                    throw new CommandException("BARCODE_FORMAT_UNSUPPORTED", "Use code39 or code128.");
                var code = VectorCodeRenderer.Encode(format, value, width, dpi.Value,
                    V2Request.Number(block, "sizeMm") ?? 24, V2Request.Number(block, "heightMm") ?? 12);
                var align = block.TryGetProperty("align", out _) ? V2Request.String(block, "align") : "center";
                var showText = type == "barcode";
                if (block.TryGetProperty("showText", out var label))
                {
                    if (label.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new CommandException("CONTENT_INVALID", "showText must be boolean.");
                    showText = label.GetBoolean();
                }
                fragment = VectorCodeRenderer.ToHtml(code, value, align, showText);
            }
            else throw new CommandException("CONTENT_TYPE_UNSUPPORTED", "Use html, qrcode or barcode blocks. Image input is unsupported.");
            html.Append("<section style=\"display:block;margin:0 0 8px\">").Append(fragment).Append("</section>");
        }
        return html.ToString();
    }

    public async Task<(object Job, bool Created)> SubmitAsync(V2Request request, CancellationToken token)
    {
        var body = request.Body;
        V2Request.Fields(body, "type", "printer", "renderId", "idempotencyKey", "metadata", "bytesBase64");
        var key = V2Request.String(body, "idempotencyKey", 200);
        var id = JobId(key);
        var type = V2Request.String(body, "type");
        var name = V2Request.String(body, "printer");
        var prior = jobs.GetCommand(id);
        var metadata = ReadMetadata(body);
        if (jobs.ReplayArchived(id, request.Digest) is { } archived)
            return (JobView(archived), false);
        PreparedReceipt? receipt = null;
        string commandHex = "";
        if (type == "print")
        {
            if (body.TryGetProperty("bytesBase64", out _)) throw new CommandException("FIELD_UNSUPPORTED", "Print jobs cannot contain raw bytes.");
            var renderId = V2Request.String(body, "renderId");
            // A lost ACK must still replay after the preview TTL. The accepted layout is part of the durable command.
            receipt = prior?.Prepared?.Id == renderId ? prior.Prepared : renders.Get(renderId, allowExpired: prior is not null);
            if (!name.Equals(receipt.Printer, StringComparison.OrdinalIgnoreCase))
                throw new CommandException("RENDER_PRINTER_MISMATCH", "This preview belongs to a different Windows queue.");
            name = receipt.Printer;
        }
        else if (type is "beep" or "open_cash_drawer" or "cut" or "send_command")
        {
            if (body.TryGetProperty("renderId", out _)) throw new CommandException("FIELD_UNSUPPORTED", "Device commands cannot contain a render ID.");
            if (type == "send_command") commandHex = ReadRaw(body);
            else if (body.TryGetProperty("bytesBase64", out _)) throw new CommandException("FIELD_UNSUPPORTED", "Use send_command for custom bytes.");
            if (prior is not null && type != "send_command") commandHex = prior.Command;
        }
        else throw new CommandException("TYPE_INVALID", "Use print, beep, open_cash_drawer, cut or send_command.");

        if (prior is null)
        {
            var printer = await FindPrinterAsync(name, token);
            name = printer.Name;
            var profile = Profile(name);
            if (receipt is not null && receipt.ProfileVersion != LayoutVersion(printer, await driverSettings.ReadAsync(name, token)))
                throw new CommandException("PRINTER_SETTINGS_CHANGED", "Windows printer settings changed since preview; prepare the receipt again.");
            if (type != "print")
            {
                if (type == "send_command" && profile?.AllowRawCommands != true)
                    throw new CommandException("COMMAND_UNSUPPORTED", "Custom raw commands are not enabled for this queue.");
                if (type != "send_command") commandHex = type switch
                {
                    "beep" => profile?.BeepCommandHex,
                    "open_cash_drawer" => profile?.DrawerCommandHex,
                    "cut" when profile?.CutMode != "driver" => profile?.CutCommandHex,
                    _ => null
                } ?? throw new CommandException("COMMAND_UNSUPPORTED", "This operation requires a verified command in the printer profile.");
                commandHex = NormalizeHex(commandHex);
            }
        }
        else if (prior.Printer.Equals(name, StringComparison.OrdinalIgnoreCase)) name = prior.Printer;
        var accepted = processor.AcceptValidated(new AcceptedCommand(id, type, name, "", "", commandHex, null, receipt, key, metadata, request.Digest));
        return (JobView(accepted.Job), accepted.Created);
    }

    private static string ReadMetadata(JsonElement body)
    {
        if (!body.TryGetProperty("metadata", out var metadata)) return "{}";
        V2Request.Fields(metadata, "station", "operator", "orderID", "ticketNumber", "ticketType", "source", "title", "itemUniques", "itemNames");
        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name is "itemUniques" or "itemNames")
            {
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() > 500
                    || property.Value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || item.GetString()!.Length > 256))
                    throw new CommandException("METADATA_INVALID", "Metadata lists must contain at most 500 short strings.");
            }
            else V2Request.String(metadata, property.Name, 256, required: false);
        }
        return V2Request.CanonicalJson(metadata);
    }

    private static string ReadRaw(JsonElement body)
    {
        var encoded = V2Request.String(body, "bytesBase64", 5500);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new CommandException("COMMAND_INVALID", "Raw command bytes must be base64."); }
        if (bytes.Length is < 1 or > 4096) throw new CommandException("COMMAND_INVALID", "Raw commands must contain 1–4096 bytes.");
        return Convert.ToHexString(bytes);
    }

    private static string NormalizeHex(string hex)
    {
        var compact = string.Concat(hex.Where(c => !char.IsWhiteSpace(c)));
        if (compact.Length is < 2 or > 8192 || compact.Length % 2 != 0 || compact.Any(c => !char.IsAsciiHexDigit(c)))
            throw new CommandException("COMMAND_INVALID", "Printer profile contains invalid command bytes.");
        return compact.ToUpperInvariant();
    }

    public static string JobId(string key) => "v2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    public async Task<(object Job, bool Created)> ReprintAsync(string originalId, V2Request request, CancellationToken token)
    {
        V2Request.Fields(request.Body, "idempotencyKey");
        var key = V2Request.String(request.Body, "idempotencyKey", 200);
        var id = JobId(key);
        var existing = jobs.GetCommand(id);
        if (existing is not null)
        {
            if (existing.ReprintOf != originalId) throw new CommandException("IDEMPOTENCY_CONFLICT", "This key belongs to a different print intent.");
            if (jobs.ReplayArchived(id, request.Digest) is { } archived)
                return (JobView(archived), false);
            var replay = processor.AcceptValidated(existing with { RequestDigest = request.Digest });
            return (JobView(replay.Job), false);
        }
        var original = jobs.ReprintSource(originalId);
        var printer = await FindPrinterAsync(original.Printer, token);
        if (original.Prepared!.ProfileVersion != LayoutVersion(printer, await driverSettings.ReadAsync(printer.Name, token)))
            throw new CommandException("PRINTER_SETTINGS_CHANGED", "Windows printer settings changed. Prepare and review a new receipt before printing with those settings.");
        var accepted = processor.AcceptValidated(original with { Id = id, IdempotencyKey = key, RequestDigest = request.Digest, ReprintOf = originalId });
        return (JobView(accepted.Job), accepted.Created);
    }

    public object GetJob(string id) => JobView(jobs.Get(id) ?? throw new CommandException("JOB_NOT_FOUND", "Job was not found."));
    public object GetJobByKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
            throw new CommandException("REQUEST_INVALID", "idempotencyKey must contain 1–200 characters.");
        return GetJob(JobId(key));
    }
    public object ListJobs(JobHistoryQuery query)
    {
        var page = jobs.History(query, query.ReadCursor(identity.ServiceId));
        return new { items = page.Items.Select(item => JobView(item.Job, item.Command)).ToArray(),
            nextCursor = page.HasMore ? query.WriteCursor(identity.ServiceId, page.Items[^1].Job) : null };
    }
    public object GetRender(string id)
    {
        var command = jobs.GetCommand(id) ?? throw new CommandException("JOB_NOT_FOUND", "Job was not found.");
        if (jobs.Get(id)!.ArtifactExpiredAt.HasValue)
            throw new CommandException("ARTIFACT_EXPIRED", "The retained receipt layout has expired. Job history and duplicate protection remain available.");
        return PreparedReceiptStore.View(command.Prepared ?? throw new CommandException("RENDER_NOT_AVAILABLE", "This job has no receipt rendering."));
    }

    internal object EventView(DurablePrintEvent entry)
    {
        object data;
        if (entry.Type == "job")
        {
            var job = entry.Data.GetProperty("job").Deserialize<JobRecord>() ?? throw new InvalidDataException("Invalid job event.");
            string? Field(string key) => entry.Data.GetProperty(key).GetString();
            var command = new AcceptedCommand(job.Id, job.Type, job.Printer, "", "", "", null,
                IdempotencyKey: Field("IdempotencyKey"), MetadataJson: Field("MetadataJson"), RequestDigest: Field("RequestDigest"), ReprintOf: Field("ReprintOf"));
            data = JobView(job, command);
        }
        else data = entry.Data;
        return new { id = EventStream.Cursor(identity.ServiceId, entry.Sequence), identity.ServiceId, entry.Type,
            entry.EntityId, entry.Version, entry.OccurredAt, data };
    }

    private object JobView(JobRecord job, AcceptedCommand? command = null)
    {
        command ??= jobs.GetCommand(job.Id, false)!;
        using var metadata = JsonDocument.Parse(command.MetadataJson ?? "{}");
        return new
        {
            job.Id, identity.ServiceId, job.Type, job.Printer, job.RenderId, command.IdempotencyKey,
            state = job.Status == "queued" ? "accepted" : job.Status, metadata = metadata.RootElement.Clone(), command.ReprintOf,
            integrity = new { verified = command.RequestDigest is not null, algorithm = "sha256", requestDigest = command.RequestDigest },
            delivery = new { state = job.SpoolerState == "unknown" ? "unknown" : job.SpoolerHandoffCompletedAt.HasValue ? "sent" : job.WindowsDocumentName is not null ? "unknown" : "not_sent",
                evidence = job.SpoolerJobId.HasValue ? "windows_spooler" : "none", job.SpoolerJobId },
            spooler = new { state = job.SpoolerState, jobId = job.SpoolerJobId, queue = job.SpoolerQueue, observedAt = job.SpoolerObservedAt,
                windowsStatus = job.WindowsStatus, statusText = job.WindowsStatusText },
            reason = job.Detail, job.Version, job.AcceptedAt, job.UpdatedAt,
            job.ArtifactExpiresAt, job.ArtifactExpiredAt
        };
    }
}
