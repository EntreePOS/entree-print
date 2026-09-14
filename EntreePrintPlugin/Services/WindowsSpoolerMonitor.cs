using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EntreePrintPlugin.Services;

public sealed class WindowsSpoolerMonitor(JobStore jobs, ILogger<WindowsSpoolerMonitor> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, Lazy<QueueWatch>> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    // Called on the delivery worker BEFORE StartDoc, so very short jobs can be observed.
    public void EnsureQueue(string printer)
    {
        if (string.IsNullOrWhiteSpace(printer) || _shutdown.IsCancellationRequested) return;
        try
        {
            _ = _queues.GetOrAdd(printer, name => new Lazy<QueueWatch>(() => new QueueWatch(name, jobs, logger, _shutdown.Token))).Value;
        }
        catch (Exception error)
        {
            _queues.TryRemove(printer, out _);
            logger.LogWarning(error, "Windows job notifications unavailable for {Printer}; monitoring will retry.", printer);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var pair in _queues.ToArray())
                    if (pair.Value.IsValueCreated && pair.Value.Value.Completion.IsCompleted) _queues.TryRemove(pair.Key, out _);
                // Reopen queue watches for persisted jobs after service/spooler restart.
                foreach (var printer in jobs.MonitoredJobs().Select(job => job.SpoolerQueue!).Distinct(StringComparer.OrdinalIgnoreCase))
                    await Task.Run(() => EnsureQueue(printer), stoppingToken);
                foreach (var job in jobs.MonitoredJobs())
                    if (DateTimeOffset.UtcNow - (job.SpoolerObservedAt ?? job.SpoolerStartedAt) > TimeSpan.FromSeconds(30))
                        jobs.MarkSpoolerUnknown(job.Id, "Windows job status has not been refreshed; completion is unknown.");
                await Task.Delay(2000, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { _shutdown.Cancel(); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        await base.StopAsync(cancellationToken);
        await Task.WhenAll(_queues.Values.Where(value => value.IsValueCreated).Select(value => value.Value.Completion)).WaitAsync(cancellationToken);
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        base.Dispose();
        _ = DisposeAfterWorkersAsync();
    }

    private async Task DisposeAfterWorkersAsync()
    {
        try
        {
            if (ExecuteTask is not null) await ExecuteTask;
            await Task.WhenAll(_queues.Values.Where(value => value.IsValueCreated).Select(value => value.Value.Completion));
        }
        catch (Exception error) { logger.LogDebug(error, "Windows job monitor stopped during disposal."); }
        finally { _shutdown.Dispose(); }
    }

    public static string StateFromFlags(uint flags)
    {
        if ((flags & 0x1080) != 0) return "completed"; // PRINTED or COMPLETE: Windows evidence only.
        if ((flags & 0x104) != 0) return "needs_attention"; // Deleted/deleting is not proof of no output.
        if ((flags & 0x663) != 0) return "blocked"; // Pause, error, offline, paper-out, blocked, intervention.
        if ((flags & 0x10) != 0) return "printing";
        return "submitted";
    }

    internal sealed record QueueJob(uint Id, string Document, uint Flags, string? StatusText = null);

    internal static void ReconcileUnidentifiedJobs(JobStore jobs, string queue, IReadOnlyList<QueueJob> snapshot)
    {
        foreach (var job in jobs.MonitoredJobs().Where(job => job.SpoolerJobId is null
            && string.Equals(job.SpoolerQueue, queue, StringComparison.OrdinalIgnoreCase)))
        {
            var matches = snapshot.Where(item => item.Id != 0 && item.Document == job.WindowsDocumentName).Take(2).ToArray();
            if (matches.Length == 1)
                jobs.ReconcileSpoolerJob(job.Id, queue, matches[0].Id, matches[0].Document, matches[0].Flags, matches[0].StatusText);
            else if (matches.Length > 1)
                jobs.MarkSpoolerUnknown(job.Id, "Multiple Windows jobs match this submission identity; check the queue before reprinting.");
            // No match is not evidence of no output. Retain uncertainty and never resubmit.
        }
    }

    internal static IReadOnlyList<QueueJob> ReadQueueJobs(string printer)
    {
        if (!OpenPrinter(printer, out var handle, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return ReadQueueJobs(handle); }
        finally { ClosePrinter(handle); }
    }

    private static IReadOnlyList<QueueJob> ReadQueueJobs(IntPtr handle)
    {
        // Request one more than our cap to detect an incomplete snapshot. Never
        // bind a seemingly unique match from a truncated view of the queue.
        const uint maxJobs = 8192;
        var success = EnumJobs(handle, 0, maxJobs + 1, 1, IntPtr.Zero, 0, out var needed, out var returned);
        var error = Marshal.GetLastWin32Error();
        if (success && needed == 0 && returned == 0) return [];
        if (!success && error != 122) throw new Win32Exception(error);
        if (needed == 0 || needed > 8 * 1024 * 1024) throw new InvalidDataException("Windows queue snapshot exceeded its limit.");
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumJobs(handle, 0, maxJobs + 1, 1, buffer, needed, out _, out returned))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var size = Marshal.SizeOf<JobInfo>();
            if (returned > maxJobs || (ulong)returned * (uint)size > needed)
                throw new InvalidDataException("Windows queue snapshot was incomplete or invalid.");
            var result = new List<QueueJob>((int)returned);
            for (var i = 0; i < returned; i++)
            {
                var job = Marshal.PtrToStructure<JobInfo>(IntPtr.Add(buffer, i * size));
                result.Add(new(job.Id, Marshal.PtrToStringUni(job.Document) ?? "", job.Status, Marshal.PtrToStringUni(job.StatusText)));
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private sealed class QueueWatch
    {
        private readonly string _printer;
        private readonly JobStore _jobs;
        private readonly ILogger _logger;
        private readonly CancellationToken _token;
        private readonly IntPtr _printerHandle;
        private readonly IntPtr _notification;
        private readonly SpoolerNotificationBuffer _observed = new();
        private readonly Dictionary<uint, SpoolerNotificationBuffer.Observation> _applied = new();
        public Task Completion { get; }

        public QueueWatch(string printer, JobStore jobs, ILogger logger, CancellationToken token)
        {
            _printer = printer; _jobs = jobs; _logger = logger; _token = token;
            if (!OpenPrinter(printer, out _printerHandle, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var fields = Marshal.AllocHGlobal(4);
            var types = Marshal.AllocHGlobal(Marshal.SizeOf<NotifyOptionsType>());
            try
            {
                Marshal.WriteInt16(fields, 0, 0x0A); // status
                Marshal.WriteInt16(fields, 2, 0x0D); // document correlation name
                Marshal.StructureToPtr(new NotifyOptionsType { Type = 1, Count = 2, Fields = fields }, types, false);
                var options = new NotifyOptions { Version = 2, Count = 1, Types = types };
                _notification = FindFirstPrinterChangeNotification(_printerHandle, 0x0000FF00, 0, ref options);
                if (_notification == IntPtr.Zero || _notification == new IntPtr(-1))
                {
                    _notification = IntPtr.Zero;
                    _logger.LogWarning("Notifications unavailable for {Printer} (error {Error}); using job queries until the next watch attempt.", printer, Marshal.GetLastWin32Error());
                }
            }
            catch { ClosePrinter(_printerHandle); throw; }
            finally { Marshal.FreeHGlobal(fields); Marshal.FreeHGlobal(types); }
            Completion = Task.Run(ReadLoop);
        }

        private void ReadLoop()
        {
            try
            {
                var nextQuery = DateTimeOffset.MinValue;
                var nextIdentityQuery = DateTimeOffset.MinValue;
                var openedAt = DateTimeOffset.UtcNow;
                while (!_token.IsCancellationRequested)
                {
                    if (_notification == IntPtr.Zero)
                    {
                        if (_token.WaitHandle.WaitOne(200)) break;
                        if (DateTimeOffset.UtcNow - openedAt > TimeSpan.FromMinutes(1)) break; // Retry notification registration.
                    }
                    else
                    {
                        var wait = WaitForSingleObject(_notification, 200);
                        if (wait == 0) ReadNotifications();
                        else if (wait != 258) throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    var active = _jobs.MonitoredJobs().Where(job => job.SpoolerQueue!.Equals(_printer, StringComparison.OrdinalIgnoreCase)).ToArray();
                    foreach (var job in active)
                    {
                        if (job.SpoolerJobId is not uint id) continue;
                        if (_observed.Get(id) is { Document: not null, Flags: uint flags } observation
                            && observation.Document == job.WindowsDocumentName && !ReferenceEquals(_applied.GetValueOrDefault(id), observation))
                        {
                            _jobs.ApplySpoolerObservation(job.Id, id, observation.Document, flags, "notification");
                            _applied[id] = observation;
                        }
                    }
                    if (_applied.Count > 4096) _applied.Clear();
                    if (DateTimeOffset.UtcNow < nextQuery) continue;
                    nextQuery = DateTimeOffset.UtcNow.AddSeconds(1);
                    if (DateTimeOffset.UtcNow >= nextIdentityQuery && active.Any(job => job.SpoolerJobId is null))
                    {
                        nextIdentityQuery = DateTimeOffset.UtcNow.AddSeconds(5);
                        try { ReconcileUnidentifiedJobs(_jobs, _printer, ReadQueueJobs(_printerHandle)); }
                        catch (Exception error)
                        {
                            _logger.LogWarning(error, "Windows queue identity lookup failed for {Printer}; known job monitoring continues.", _printer);
                        }
                        active = _jobs.MonitoredJobs().Where(job => job.SpoolerQueue!.Equals(_printer, StringComparison.OrdinalIgnoreCase)).ToArray();
                    }
                    foreach (var job in active)
                    {
                        if (job.SpoolerJobId is not uint id) continue;
                        var result = Query(id);
                        if (result is not null)
                        {
                            if (result.Value.Document == job.WindowsDocumentName)
                                _jobs.ApplySpoolerObservation(job.Id, id, result.Value.Document, result.Value.Flags, "query", result.Value.StatusText);
                            else _jobs.MarkSpoolerUnknown(job.Id, "Windows job identity no longer matches the submitted receipt.");
                        }
                        else if (DateTimeOffset.UtcNow - job.SpoolerStartedAt > TimeSpan.FromSeconds(10))
                            _jobs.MarkSpoolerUnknown(job.Id, "Windows job disappeared without captured completion evidence.");
                    }
                }
            }
            catch (Exception error) { _logger.LogWarning(error, "Windows queue watch ended for {Printer}; reconciliation will retry.", _printer); }
            finally { if (_notification != IntPtr.Zero) FindClosePrinterChangeNotification(_notification); ClosePrinter(_printerHandle); }
        }

        private void ReadNotifications()
        {
            if (!FindNextPrinterChangeNotification(_notification, out _, IntPtr.Zero, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (info == IntPtr.Zero) return;
            var refresh = false;
            try
            {
                var header = Marshal.PtrToStructure<NotifyInfo>(info);
                if ((header.Flags & 1) != 0) { _observed.Clear(); refresh = true; }
                else
                {
                    if (header.Count > 8192) throw new InvalidDataException("Windows notification batch exceeded its limit.");
                    var offset = Marshal.OffsetOf<NotifyInfo>(nameof(NotifyInfo.First)).ToInt32();
                    var entries = new List<SpoolerNotificationBuffer.Entry>();
                    for (var i = 0; i < header.Count; i++)
                    {
                        var entry = Marshal.PtrToStructure<NotifyData>(IntPtr.Add(info, offset + i * Marshal.SizeOf<NotifyData>()));
                        if (entry.Type != 1) continue;
                        if (entry.Field == 0x0D) entries.Add(new(entry.Id, Document: ReadText(entry.Data.Buffer)));
                        if (entry.Field == 0x0A) entries.Add(new(entry.Id, Flags: entry.Data.Number));
                    }
                    _observed.Apply(entries);
                }
            }
            finally { FreePrinterNotifyInfo(info); }
            if (refresh)
            {
                var options = new NotifyOptions { Version = 2, Flags = 1 };
                var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NotifyOptions>());
                try
                {
                    Marshal.StructureToPtr(options, pointer, false);
                    if (!FindNextPrinterChangeNotification(_notification, out _, pointer, out var snapshot)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (snapshot != IntPtr.Zero) FreePrinterNotifyInfo(snapshot); // Query each tracked job next.
                }
                finally { Marshal.FreeHGlobal(pointer); }
            }
        }

        private (string Document, uint Flags, string? StatusText)? Query(uint id)
        {
            GetJob(_printerHandle, id, 1, IntPtr.Zero, 0, out var needed);
            var error = Marshal.GetLastWin32Error();
            if (needed == 0)
            {
                if (error is 87 or 2) return null;
                throw new Win32Exception(error);
            }
            if (needed > 1_000_000) throw new InvalidDataException("Windows job info exceeded its limit.");
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetJob(_printerHandle, id, 1, buffer, needed, out _))
                {
                    error = Marshal.GetLastWin32Error();
                    if (error is 87 or 2) return null;
                    throw new Win32Exception(error);
                }
                var job = Marshal.PtrToStructure<JobInfo>(buffer);
                return (Marshal.PtrToStringUni(job.Document) ?? "", job.Status, Marshal.PtrToStringUni(job.StatusText));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static string? ReadText(NotifyBuffer buffer) => buffer.Pointer == IntPtr.Zero || buffer.Length > 65536
            ? null : Marshal.PtrToStringUni(buffer.Pointer, (int)buffer.Length / 2)?.TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential)] private struct NotifyOptions { public uint Version, Flags, Count; public IntPtr Types; }
    [StructLayout(LayoutKind.Sequential)] private struct NotifyOptionsType { public ushort Type, Reserved0; public uint Reserved1, Reserved2, Count; public IntPtr Fields; }
    [StructLayout(LayoutKind.Sequential)] private struct NotifyBuffer { public uint Length; public IntPtr Pointer; }
    [StructLayout(LayoutKind.Explicit)] private struct NotifyUnion { [FieldOffset(0)] public uint Number; [FieldOffset(0)] public NotifyBuffer Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct NotifyData { public ushort Type, Field; public uint Reserved, Id; public NotifyUnion Data; }
    [StructLayout(LayoutKind.Sequential)] private struct NotifyInfo { public uint Version, Flags, Count; public NotifyData First; }
    [StructLayout(LayoutKind.Sequential)] private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }
    [StructLayout(LayoutKind.Sequential)] private struct JobInfo
    {
        public uint Id;
        public IntPtr Printer, Machine, User, Document, DataType, StatusText;
        public uint Status, Priority, Position, TotalPages, PagesPrinted;
        public SystemTime Submitted;
    }
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool OpenPrinter(string name, out IntPtr printer, IntPtr defaults);
    [DllImport("winspool.drv")] private static extern bool ClosePrinter(IntPtr printer);
    [DllImport("winspool.drv", SetLastError = true)] private static extern IntPtr FindFirstPrinterChangeNotification(IntPtr printer, uint filter, uint options, ref NotifyOptions notifyOptions);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool FindNextPrinterChangeNotification(IntPtr change, out uint flags, IntPtr options, out IntPtr info);
    [DllImport("winspool.drv")] private static extern bool FindClosePrinterChangeNotification(IntPtr change);
    [DllImport("winspool.drv")] private static extern bool FreePrinterNotifyInfo(IntPtr info);
    [DllImport("winspool.drv", EntryPoint = "GetJobW", SetLastError = true)] private static extern bool GetJob(IntPtr printer, uint id, uint level, IntPtr info, uint size, out uint needed);
    [DllImport("winspool.drv", EntryPoint = "EnumJobsW", SetLastError = true)] private static extern bool EnumJobs(IntPtr printer, uint first, uint count, uint level, IntPtr info, uint size, out uint needed, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
