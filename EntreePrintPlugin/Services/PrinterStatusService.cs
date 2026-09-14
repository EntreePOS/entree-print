using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EntreePrintPlugin.Models;

namespace EntreePrintPlugin.Services;

public sealed class PrinterStatusService(
    PluginSettings settings,
    EventBroadcaster events,
    ILogger<PrinterStatusService> logger) : BackgroundService, IPrinterInventory
{
    private volatile PrinterStatusRecord[] _printers = [];
    private readonly object _refreshGate = new();
    private Task<IReadOnlyList<PrinterStatusRecord>>? _refreshTask;
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public IReadOnlyList<PrinterStatusRecord> GetCachedPrinters()
    {
        return _printers
            .OrderBy(printer => printer.Name, StringComparer.OrdinalIgnoreCase)
            .Select(printer => printer with { Stale = DateTimeOffset.UtcNow - printer.UpdatedAt > TimeSpan.FromSeconds(Math.Max(15, settings.PrinterStatusRefreshSeconds * 3)) })
            .ToArray();
    }

    public PrinterStatusRecord? GetCachedPrinter(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
        {
            return GetCachedPrinters().FirstOrDefault(printer => printer.Default == true);
        }

        return GetCachedPrinters().FirstOrDefault(printer => printer.Name.Equals(printerName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public Task<IReadOnlyList<PrinterStatusRecord>> RefreshNowAsync(CancellationToken cancellationToken)
    {
        // Clients share one query. A caller timing out must not cancel another client's refresh.
        lock (_refreshGate)
        {
            if (_refreshTask is null || _refreshTask.IsCompleted) _refreshTask = RefreshCoreAsync();
            return _refreshTask.WaitAsync(cancellationToken);
        }
    }

    private async Task<IReadOnlyList<PrinterStatusRecord>> RefreshCoreAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var cancellationToken = timeout.Token;
        var records = await QueryPrintersAsync(cancellationToken);
        _printers = records.ToArray(); // Publish a complete inventory; never expose a partly rebuilt list.
        var snapshot = GetCachedPrinters();
        events.Publish("printers", snapshot);
        foreach (var record in snapshot)
        {
            events.Publish("printer", record);
        }
        return snapshot;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        await base.StopAsync(cancellationToken);
        Task? refresh;
        lock (_refreshGate) refresh = _refreshTask;
        if (refresh is not null)
            try { await refresh.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        base.Dispose();
        _ = DisposeAfterRefreshAsync();
    }

    private async Task DisposeAfterRefreshAsync()
    {
        try
        {
            if (ExecuteTask is not null) await ExecuteTask;
            Task? refresh;
            lock (_refreshGate) refresh = _refreshTask;
            if (refresh is not null) await refresh;
        }
        catch (Exception error) { logger.LogDebug(error, "Printer status worker stopped during disposal."); }
        finally { _shutdown.Dispose(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshNowAsync(stoppingToken);
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Printer status refresh failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.PrinterStatusRefreshSeconds)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<IReadOnlyList<PrinterStatusRecord>> QueryPrintersAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var script = """
$ErrorActionPreference = 'Stop'
$printers = Get-Printer | Select-Object Name,PrinterStatus,WorkOffline,PrinterState,DriverName,PortName
$cim = Get-CimInstance Win32_Printer | Select-Object Name,Default,PrinterStatus,DetectedErrorState,ExtendedPrinterStatus,PrinterState,WorkOffline,DriverName,PortName
$ports = Get-PrinterPort | Select-Object Name,PrinterHostAddress,PortNumber
[pscustomobject]@{ printers = $printers; cim = $cim; ports = $ports } | ConvertTo-Json -Compress -Depth 5
""";

        var output = await RunPowerShellAsync(script, cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException("Windows printer query returned no inventory payload.");
        }

        return ParseStatusPayload(output);
    }

    internal static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell",
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
                "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false);\n" + script },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (process is null)
        {
            throw new InvalidOperationException("Windows printer query process could not start.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"PowerShell printer query failed: {error}");
        }

        return output;
    }

    internal static IReadOnlyList<PrinterStatusRecord> ParseStatusPayload(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var cimByName = ReadArray(root, "cim")
            .Where(item => ReadString(item, "Name").Length > 0)
            .ToDictionary(item => ReadString(item, "Name"), StringComparer.OrdinalIgnoreCase);
        var portByName = ReadArray(root, "ports")
            .Where(item => ReadString(item, "Name").Length > 0)
            .ToDictionary(item => ReadString(item, "Name"), StringComparer.OrdinalIgnoreCase);

        var records = new List<PrinterStatusRecord>();
        foreach (var printer in ReadArray(root, "printers"))
        {
            var name = ReadString(printer, "Name");
            if (name.Length == 0)
            {
                continue;
            }

            cimByName.TryGetValue(name, out var cim);
            portByName.TryGetValue(FirstNonEmpty(ReadString(printer, "PortName"), ReadString(cim, "PortName")), out var port);
            records.Add(ToRecord(printer, cim, port));
        }

        foreach (var missing in cimByName.Values.Where(cim => records.All(record => !record.Name.Equals(ReadString(cim, "Name"), StringComparison.OrdinalIgnoreCase))))
        {
            portByName.TryGetValue(ReadString(missing, "PortName"), out var port);
            records.Add(ToRecord(missing, missing, port, nativeStatus: false));
        }

        return records;
    }

    private static PrinterStatusRecord ToRecord(JsonElement printer, JsonElement? cim, JsonElement? port, bool nativeStatus = true)
    {
        var name = ReadString(printer, "Name");
        var rawPrinterStatus = FirstNonEmpty(ReadString(printer, "PrinterStatus"), ReadString(cim, "PrinterStatus"), "unknown");
        var cimStatus = ReadInt(cim, "PrinterStatus");
        var state = ReadInt(printer, "PrinterState") ?? ReadInt(cim, "PrinterState");
        // Get-Printer reports a Windows flags enum; Win32_Printer.PrinterStatus is a different CIM enum.
        var flags = nativeStatus ? ReadInt(printer, "PrinterStatus") ?? state : state;
        var detectedErrorState = ReadInt(cim, "DetectedErrorState");
        var extendedPrinterStatus = ReadInt(cim, "ExtendedPrinterStatus");
        var offline = ReadBool(printer, "WorkOffline") == true || ReadBool(cim, "WorkOffline") == true
            || HasText(rawPrinterStatus, "offline") || HasPrinterStateBit(flags, 0x80) || detectedErrorState == 9 || cimStatus == 7 || extendedPrinterStatus == 7;
        var paused = HasText(rawPrinterStatus, "paused") || HasPrinterStateBit(flags, 0x1) || extendedPrinterStatus == 8;
        var paperOut = HasText(rawPrinterStatus, "paperout") || HasText(rawPrinterStatus, "paper out")
            || HasPrinterStateBit(flags, 0x10) || detectedErrorState == 4;
        var paperLow = detectedErrorState == 3;
        var doorOpen = detectedErrorState == 7 || HasPrinterStateBit(flags, 0x400000);
        var error = HasText(rawPrinterStatus, "error") ||
                    HasPrinterStateBit(flags, 0x00000002) || HasPrinterStateBit(flags, 0x8) ||
                    detectedErrorState is 6 or 8 or 10 or 11;
        var status = NormalizeStatus(rawPrinterStatus, nativeStatus ? (flags == 0 ? 0 : null) : cimStatus,
            offline, paperOut, paused, doorOpen, error);
        if (status == "ready" && paperLow) status = "paper_low";
        if (status == "ready" && (HasPrinterStateBit(flags, 0x400) || cimStatus == 4)) status = "printing";

        return new PrinterStatusRecord
        {
            Name = name,
            SystemName = name,
            Status = status,
            Offline = offline ? true : null,
            PaperOut = paperOut ? true : detectedErrorState == 2 ? false : null,
            PaperLow = paperLow ? true : detectedErrorState == 2 ? false : null,
            CoverOpen = doorOpen ? true : null,
            DrawerOpen = null,
            Paused = paused ? true : flags.HasValue ? false : null,
            Error = error ? true : detectedErrorState == 2 ? false : null,
            Default = ReadBool(cim, "Default"),
            DriverName = FirstNonEmpty(ReadString(printer, "DriverName"), ReadString(cim, "DriverName")),
            PortName = FirstNonEmpty(ReadString(printer, "PortName"), ReadString(cim, "PortName")),
            HostAddress = ReadString(port, "PrinterHostAddress"),
            PortNumber = ReadInt(port, "PortNumber"),
            RawPrinterStatus = rawPrinterStatus,
            PrinterState = state,
            DetectedErrorState = detectedErrorState,
            ExtendedPrinterStatus = extendedPrinterStatus,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static string NormalizeStatus(string status, int? numericStatus, bool offline, bool paperOut, bool paused, bool doorOpen, bool error)
    {
        if (doorOpen) return "cover_open";
        if (paperOut) return "paper_out";
        if (offline) return "offline";
        if (paused) return "paused";
        if (error) return "error";
        if (numericStatus is 0 or 3)
        {
            return "ready";
        }
        if (HasText(status, "normal") || HasText(status, "idle") || HasText(status, "ready"))
        {
            return "ready";
        }
        return string.IsNullOrWhiteSpace(status) ? "unknown" : status.ToLowerInvariant();
    }

    private static IReadOnlyList<JsonElement> ReadArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }
        return property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().ToArray()
            : [property];
    }

    private static string ReadString(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object || !element.Value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return "";
        }
        return property.ToString();
    }

    private static int? ReadInt(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object || !element.Value.TryGetProperty(name, out var property))
        {
            return null;
        }
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value) ? value : null;
    }

    private static bool? ReadBool(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object || !element.Value.TryGetProperty(name, out var property))
        {
            return null;
        }
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool HasText(string value, string expected)
    {
        return value.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasPrinterStateBit(int? state, int bit)
    {
        return state.HasValue && (state.Value & bit) != 0;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }
}
