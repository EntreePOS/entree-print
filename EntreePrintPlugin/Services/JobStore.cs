using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntreePrintPlugin.Models;

namespace EntreePrintPlugin.Services;

// One atomic, flushed document owns each accepted intent and its recovery state.
public sealed class JobStore : IDisposable
{
    private readonly EventBroadcaster _events;
    private readonly string? _directory;
    private readonly FileStream? _owner;
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _artifactRetention;
    private readonly (int Jobs, int PreparedJobs, long Bytes) _limits;
    private readonly Dictionary<string, StoredJob> _jobs = new(StringComparer.Ordinal);
    public const int MaxRetainedJobs = 100000;
    public const int MaxPreparedJobs = 10000;
    public const long MaxAcceptanceBytes = 512_000_000;
    private long _storedBytes;
    private int _preparedCount;
    private bool _disposed;

    public JobStore(EventBroadcaster events, string? directory = null, TimeProvider? clock = null, int receiptRetentionDays = 7)
        : this(events, directory, clock, receiptRetentionDays, (MaxRetainedJobs, MaxPreparedJobs, MaxAcceptanceBytes)) { }

    internal JobStore(EventBroadcaster events, string? directory, TimeProvider? clock, int receiptRetentionDays,
        (int Jobs, int PreparedJobs, long Bytes) limits)
    {
        if (receiptRetentionDays is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(receiptRetentionDays));
        if (limits.Jobs < 1 || limits.PreparedJobs < 1 || limits.Bytes < 1) throw new ArgumentOutOfRangeException(nameof(limits));
        _limits = limits;
        _artifactRetention = TimeSpan.FromDays(receiptRetentionDays);
        _events = events;
        _clock = clock ?? TimeProvider.System;
        _directory = directory is null ? null : Path.GetFullPath(directory);
        if (_directory is null) return; // Only isolated tests use an in-memory store.
        Directory.CreateDirectory(_directory);
        _owner = new FileStream(Path.Combine(_directory, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                var bytes = File.ReadAllBytes(path);
                var stored = JsonSerializer.Deserialize<StoredJob>(bytes)
                    ?? throw new InvalidDataException($"Unreadable job record: {path}");
                ValidatePrepared(stored.Command);
                long priorVersion = 0;
                foreach (var pending in stored.PendingEvents ?? [])
                {
                    if (pending.Id != stored.Job.Id || pending.Version <= priorVersion || pending.Version > stored.Job.Version)
                        throw new InvalidDataException($"Invalid pending job event: {path}");
                    priorVersion = pending.Version;
                }
                if (stored.PendingEvents?.Length > 256) throw new InvalidDataException($"Too many pending job events: {path}");
                if (stored.Schema != 1 || stored.Job.Id != stored.Command.Id || stored.Hash != Hash(stored.Command, stored.ArchivedContentHash)
                    || (stored.ArchivedContentHash is null) != (stored.Job.ArtifactExpiredAt is null)
                    || (stored.ArchivedContentHash is not null && (stored.Command.Prepared is not null || !CanExpire(stored.Job)
                        || stored.Job.ArtifactExpiresAt is null || stored.Job.ArtifactExpiredAt < stored.Job.ArtifactExpiresAt))
                    || !string.Equals(path, RecordPath(stored.Job.Id), StringComparison.OrdinalIgnoreCase)
                    || !_jobs.TryAdd(stored.Job.Id, stored))
                    throw new InvalidDataException($"Invalid job record: {path}");
                _storedBytes += bytes.Length;
                if (stored.Command.Prepared is not null) _preparedCount++;
                _jobs[stored.Job.Id] = stored with { Bytes = bytes.Length };
            }
            FlushPendingEvents(throwOnFailure: false);
            foreach (var stored in _jobs.Values.ToArray())
                if (stored.Job.Status is "processing" or "submitting")
                    UpdateStatus(stored.Job, "needs_attention", "Service stopped during handoff; reconcile Windows before reprinting.");
        }
        catch { _owner.Dispose(); throw; }
    }

    public JobRecord? Get(string id)
    {
        lock (_gate) return _jobs.TryGetValue(id, out var stored) ? stored.Job with { } : null;
    }

    public (JobRecord Job, bool Created) Accept(AcceptedCommand command, int maxAttempts)
    {
        lock (_gate)
        {
            if (command.Prepared is not null) { command = Clone(command); ValidatePrepared(command); }
            var hash = Hash(command);
            if (_jobs.TryGetValue(command.Id, out var existing))
            {
                if (existing.Hash != hash) throw new CommandException("IDEMPOTENCY_CONFLICT", "This intent ID already belongs to a different command.");
                return (existing.Job with { }, false);
            }
            if (command.ReprintOf is { } originalId)
            {
                // Recheck eligibility under the same lock as acceptance: Windows status can change during inventory lookup.
                var original = ReprintSource(originalId);
                if (hash != Hash(original with { Id = command.Id, IdempotencyKey = command.IdempotencyKey,
                    RequestDigest = command.RequestDigest, ReprintOf = originalId }))
                    throw new CommandException("REPRINT_CONTENT_CHANGED", "A reprint must preserve the original receipt, metadata and destination.");
            }
            if (_jobs.Count >= _limits.Jobs || (command.Prepared is not null && _preparedCount >= _limits.PreparedJobs))
                throw new CommandException("QUEUE_FULL", "The job ledger has reached its retention limit. Existing jobs and duplicate protection are retained.");
            var now = _clock.GetUtcNow();
            var job = new JobRecord { Id = command.Id, Type = command.Type, Printer = command.Printer,
                RenderId = command.Prepared?.Id, Status = "queued", MaxAttempts = maxAttempts, AcceptedAt = now, UpdatedAt = now };
            var stored = PrepareWrite(new StoredJob(1, job, command, hash));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(stored);
            if (_storedBytes + bytes.Length > _limits.Bytes)
                throw new CommandException("QUEUE_FULL", "The receipt ledger has reached its storage budget. Existing jobs are retained.");
            try { Persist(stored); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new CommandException("STORAGE_UNAVAILABLE", "Could not durably accept this print intent."); }
            _jobs.Add(command.Id, stored with { Bytes = bytes.Length });
            _storedBytes += bytes.Length;
            if (command.Prepared is not null) _preparedCount++;
            Notify(_jobs[command.Id]);
            return (job with { }, true);
        }
    }

    public AcceptedCommand? GetCommand(string id, bool includePrepared = true)
    {
        lock (_gate) return _jobs.TryGetValue(id, out var stored)
            ? includePrepared ? Clone(stored.Command) : stored.Command with { Prepared = null } : null;
    }

    // Exact original request bytes remain replayable even after receipt geometry is removed.
    public JobRecord? ReplayArchived(string id, string digest)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var stored) || stored.Job.ArtifactExpiredAt is null) return null;
            if (stored.Command.RequestDigest is null || !stored.Command.RequestDigest.Equals(digest, StringComparison.OrdinalIgnoreCase))
                throw new CommandException("IDEMPOTENCY_CONFLICT", "This retained intent requires its original request bytes. Its receipt artifact has expired; do not reuse the key for new work.");
            return stored.Job with { };
        }
    }

    private static bool CanExpire(JobRecord job) => !job.SpoolerHandoffUncertain && job.CompletedAt.HasValue
        && ((job.Status == "completed" && job.SpoolerState is "completed" or "not_submitted")
            || (job.Status == "failed" && job.WindowsDocumentName is null));

    public int ExpireArtifacts(int limit = 100)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        string[] candidates;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            candidates = _jobs.Values.Where(stored => stored.Command.Prepared is not null && CanExpire(stored.Job)
                && (stored.Job.ArtifactExpiresAt ?? stored.Job.CompletedAt + _artifactRetention) <= now).Take(limit).Select(stored => stored.Job.Id).ToArray();
        }
        var count = 0;
        foreach (var id in candidates)
        {
            // Release the gate between files so queue/status writes can make progress during cleanup.
            lock (_gate)
            {
                var stored = _jobs[id];
                var now = _clock.GetUtcNow();
                if (stored.Command.Prepared is null || !CanExpire(stored.Job)
                    || (stored.Job.ArtifactExpiresAt ?? stored.Job.CompletedAt + _artifactRetention) > now) continue;
                var next = stored with
                {
                    Job = stored.Job with { ArtifactExpiresAt = stored.Job.ArtifactExpiresAt ?? stored.Job.CompletedAt + _artifactRetention,
                        ArtifactExpiredAt = now, ArtifactPath = null, UpdatedAt = now, Version = stored.Job.Version + 1 },
                    ArchivedContentHash = stored.Command.Prepared!.ContentHash,
                    Command = stored.Command with { Prepared = null }
                };
                next = PrepareWrite(next);
                Persist(next); // Atomic replacement: failure keeps both the layout and dedup record intact.
                var bytes = JsonSerializer.SerializeToUtf8Bytes(next).Length;
                var previousBytes = _jobs[next.Job.Id].Bytes;
                _jobs[next.Job.Id] = next with { Bytes = bytes };
                _storedBytes += bytes - previousBytes;
                _preparedCount--;
                Notify(_jobs[next.Job.Id]);
                count++;
            }
        }
        return count;
    }

    public IReadOnlyList<JobRecord> List(string? printer = null, int limit = 50)
    {
        lock (_gate) return _jobs.Values.Select(x => x.Job)
            .Where(job => printer is null || job.Printer.Equals(printer, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(job => job.AcceptedAt).Take(Math.Clamp(limit, 1, 200)).Select(job => job with { }).ToArray();
    }

    public AcceptedCommand ReprintSource(string id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var source)) throw new CommandException("JOB_NOT_FOUND", "Original job was not found.");
            if (source.Command.Type != "print" || source.Command.IdempotencyKey is null)
                throw new CommandException("JOB_NOT_REPRINTABLE", "Only receipt jobs can be reprinted; device commands require a new explicit action.");
            if (source.Job.Status is not ("completed" or "failed" or "needs_attention") || source.Job.SpoolerState is "submitting" or "submitted" or "printing" or "blocked")
                throw new CommandException("JOB_NOT_REPRINTABLE", "The original job is still active. Check its status instead of creating another copy.");
            if (source.Command.Prepared is null) throw new CommandException("ARTIFACT_EXPIRED", "The original receipt is no longer available for reprinting.");
            return Clone(source.Command);
        }
    }

    public (IReadOnlyList<(JobRecord Job, AcceptedCommand Command)> Items, bool HasMore) History(JobHistoryQuery query, JobHistoryCursor? cursor)
    {
        lock (_gate)
        {
            var matches = _jobs.Values.Where(item => item.Command.IdempotencyKey is not null
                && (query.Printer is null || item.Job.Printer.Equals(query.Printer, StringComparison.OrdinalIgnoreCase))
                && (query.Station is null || item.Metadata.Station == query.Station)
                && (query.OrderID is null || item.Metadata.OrderID == query.OrderID)
                && (query.Status.Length == 0 || query.Status.Contains(item.Job.Status == "queued" ? "accepted" : item.Job.Status, StringComparer.Ordinal))
                && (query.Since is null || item.Job.AcceptedAt >= query.Since)
                && (cursor is null || item.Job.AcceptedAt < cursor.AcceptedAt ||
                    (item.Job.AcceptedAt == cursor.AcceptedAt && string.CompareOrdinal(item.Job.Id, cursor.Id) < 0)))
                .OrderByDescending(item => item.Job.AcceptedAt).ThenByDescending(item => item.Job.Id, StringComparer.Ordinal)
                .Take(query.Limit + 1).ToArray();
            return (matches.Take(query.Limit).Select(item => (item.Job with { }, item.Command with { Prepared = null })).ToArray(), matches.Length > query.Limit);
        }
    }

    public IReadOnlyList<(AcceptedCommand Command, JobRecord Job)> RecoverableJobs()
    {
        lock (_gate) return _jobs.Values.Where(x => x.Job.Status is "queued" or "accepted" or "waiting_for_printer")
            .OrderBy(x => x.Job.AcceptedAt).Select(x => (Clone(x.Command), x.Job with { })).ToArray();
    }

    public IReadOnlyList<JobRecord> MonitoredJobs()
    {
        lock (_gate) return _jobs.Values.Select(x => x.Job)
            .Where(job => job.WindowsDocumentName is not null && job.SpoolerQueue is not null
                && job.Status is not ("completed" or "failed"))
            .Select(job => job with { }).ToArray();
    }

    // Persist a unique attempt identity BEFORE calling Windows. Client receipt titles are not unique enough.
    public string BeginSpoolerSubmission(string id, string queue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        lock (_gate)
        {
            var current = _jobs[id].Job;
            if (current.WindowsDocumentName is not null)
                throw new InvalidOperationException("A Windows submission already exists for this intent; reconcile it instead of resubmitting.");
            var document = $"ENTREE {Guid.NewGuid():N}";
            Commit(current with { WindowsDocumentName = document, SpoolerQueue = queue,
                SpoolerState = "submitting", SpoolerStartedAt = _clock.GetUtcNow(),
                Status = "submitting", UpdatedAt = _clock.GetUtcNow() });
            return document;
        }
    }

    public void RecordSpoolerJob(string id, uint spoolerJobId, string document)
    {
        if (spoolerJobId == 0) throw new ArgumentOutOfRangeException(nameof(spoolerJobId));
        lock (_gate)
        {
            var job = _jobs[id].Job;
            if (job.WindowsDocumentName != document || (job.SpoolerJobId.HasValue && job.SpoolerJobId != spoolerJobId))
                throw new InvalidOperationException("Windows submission identity changed.");
            if (job.SpoolerJobId == spoolerJobId) return;
            Commit(job with { SpoolerJobId = spoolerJobId, UpdatedAt = _clock.GetUtcNow(),
                Detail = "Windows created the document; complete handoff is pending." });
        }
    }

    // A queue snapshot can recover the StartDoc-to-ledger gap. It never proves
    // that the full receipt was handed off, and can only bind the persisted attempt.
    internal bool ReconcileSpoolerJob(string id, string queue, uint spoolerJobId, string document, uint flags, string? statusText)
    {
        if (spoolerJobId == 0) return false;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var stored)) return false;
            var job = stored.Job;
            if (job.SpoolerJobId.HasValue || job.WindowsDocumentName != document
                || !string.Equals(job.SpoolerQueue, queue, StringComparison.OrdinalIgnoreCase)
                || job.Status is "completed" or "failed") return false;
            var now = _clock.GetUtcNow();
            var next = job with { SpoolerJobId = spoolerJobId, WindowsStatus = flags,
                WindowsStatusText = statusText, SpoolerObservedAt = now,
                SpoolerEvidence = "queue_identity", UpdatedAt = now };
            ApplyObservedState(next);
            Commit(next);
            return true;
        }
    }

    // Only a typed pre-handoff rejection may remove an attempt identity and permit a retry.
    public void RecordSpoolerNotSubmitted(string id)
    {
        lock (_gate)
        {
            var job = _jobs[id].Job;
            if (job.SpoolerJobId.HasValue) throw new InvalidOperationException("Windows already created this job.");
            Commit(job with { WindowsDocumentName = null, SpoolerQueue = null, SpoolerState = "not_submitted",
                SpoolerStartedAt = null, UpdatedAt = _clock.GetUtcNow() });
        }
    }

    public void ApplySpoolerObservation(string id, uint spoolerJobId, string document, uint flags, string evidence, string? statusText = null)
    {
        lock (_gate)
        {
            var job = _jobs[id].Job;
            if (job.SpoolerJobId != spoolerJobId || job.WindowsDocumentName != document || job.SpoolerState == "completed") return;
            // Completion is monotonic for this exact attempt, even if a later query only reports deletion.
            flags |= (job.WindowsStatus ?? 0) & 0x1080;
            var now = _clock.GetUtcNow();
            if (job.WindowsStatus == flags && job.WindowsStatusText == statusText && job.SpoolerState != "unknown"
                && now - job.SpoolerObservedAt < TimeSpan.FromSeconds(30)) return;
            var next = job with { WindowsStatus = flags, WindowsStatusText = statusText,
                SpoolerObservedAt = now, SpoolerEvidence = evidence, UpdatedAt = now };
            ApplyObservedState(next);
            Commit(next);
        }
    }

    public void MarkSpoolerUnknown(string id, string reason)
    {
        lock (_gate)
        {
            var job = _jobs[id].Job;
            if (job.SpoolerState is "completed" or "unknown") return;
            Commit(job with { Status = "needs_attention", SpoolerState = "unknown", Detail = reason, Error = reason,
                UpdatedAt = _clock.GetUtcNow() });
        }
    }

    public void UpdateStatus(JobRecord job, string status, string? detailOrError = null, string? artifactPath = null)
    {
        lock (_gate)
        {
            var stored = _jobs[job.Id];
            var next = stored.Job with { Status = status, UpdatedAt = _clock.GetUtcNow(),
                Error = status is "failed" or "needs_attention" ? detailOrError : null,
                Detail = detailOrError, ArtifactPath = artifactPath ?? stored.Job.ArtifactPath,
                Attempts = job.Attempts, RetryReason = job.RetryReason, NextRetryAt = job.NextRetryAt,
                SpoolerJobId = stored.Job.SpoolerJobId ?? job.SpoolerJobId,
                CompletedAt = status is "completed" or "failed" or "rendered" or "render_pending" ? _clock.GetUtcNow() : null };
            if (stored.Job.SpoolerState == "completed")
                next = stored.Job with { ArtifactPath = next.ArtifactPath }; // Late worker returns cannot erase completion.
            else if (next.WindowsDocumentName is not null)
            {
                if (status == "submitted")
                {
                    next.SpoolerHandoffCompletedAt = _clock.GetUtcNow();
                    next.SpoolerState = "submitted";
                    if (next.WindowsStatus.HasValue) ApplyObservedState(next);
                }
                else if (status is "failed" or "needs_attention")
                {
                    next.Status = "needs_attention";
                    next.SpoolerState = "unknown";
                    next.SpoolerHandoffUncertain = next.SpoolerHandoffCompletedAt is null;
                    next.CompletedAt = null;
                    if (next.WindowsStatus.HasValue) ApplyObservedState(next);
                }
            }
            Commit(next);
            job.Status = next.Status; job.UpdatedAt = next.UpdatedAt; job.Error = next.Error;
            job.Detail = next.Detail; job.ArtifactPath = next.ArtifactPath; job.CompletedAt = next.CompletedAt;
        }
    }

    private void ApplyObservedState(JobRecord job)
    {
        var state = WindowsSpoolerMonitor.StateFromFlags(job.WindowsStatus!.Value);
        if (job.SpoolerHandoffUncertain)
        {
            // An active partial document still belongs to Windows. Block explicit
            // reprints while it remains active, without claiming a complete receipt.
            job.SpoolerState = state is "submitted" or "printing" or "blocked" ? state : "unknown";
            job.Status = "needs_attention";
            job.CompletedAt = null;
            job.Detail = job.SpoolerState == "unknown"
                ? "Windows no longer reports an active job, but full receipt handoff was not confirmed. Check the paper output before reprinting."
                : "Windows still has this job, but full receipt handoff was not confirmed. Check the Windows queue before reprinting.";
            job.Error = job.Detail;
            return;
        }
        if (job.SpoolerHandoffCompletedAt is null)
        {
            job.SpoolerState = "submitting";
            return; // Capture early evidence, publish completion only after full document handoff.
        }
        job.SpoolerState = state == "needs_attention" ? "unknown" : state;
        job.Status = state;
        job.Detail = state switch
        {
            "completed" => "Completed by Windows; physical paper output is not independently confirmed.",
            "needs_attention" => "Windows deleted or cancelled the job without completion evidence; output may be partial.",
            "blocked" => "Windows reports that this job is blocked; the existing Windows job is retained without resubmission.",
            "printing" => "Windows is processing this job.",
            _ => "Windows has this job; completion has not been reported."
        };
        job.Error = state == "needs_attention" ? job.Detail : null;
        job.CompletedAt = state == "completed" ? _clock.GetUtcNow() : null;
    }

    private void Commit(JobRecord next)
    {
        if (_jobs[next.Id].Command.Prepared is not null)
            next.ArtifactExpiresAt = CanExpire(next) ? next.ArtifactExpiresAt ?? next.CompletedAt + _artifactRetention : null;
        next = next with { Version = _jobs[next.Id].Job.Version + 1 };
        var stored = PrepareWrite(_jobs[next.Id] with { Job = next });
        Persist(stored); // No state/event is visible before the write succeeds.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(stored).Length;
        _storedBytes += bytes - _jobs[next.Id].Bytes;
        _jobs[next.Id] = stored with { Bytes = bytes };
        Notify(_jobs[next.Id]);
    }

    private StoredJob PrepareWrite(StoredJob next)
    {
        if (_jobs.TryGetValue(next.Job.Id, out var previous) && previous.PendingEvents?.Length > 0)
        {
            Notify(previous);
        }
        var pending = _jobs.GetValueOrDefault(next.Job.Id)?.PendingEvents ?? [];
        if (_events.History is null && pending.Length == 0) return next with { PendingEvents = null };
        if (pending.Length >= 256) throw new IOException("Pending job notifications reached their limit. Existing state remains retained.");
        return next with { PendingEvents = [.. pending, next.Job with { }] };
    }

    private void Notify(StoredJob stored)
    {
        if (stored.PendingEvents is not { Length: > 0 }) { _events.PublishJob(stored.Job, stored.Command); return; }
        // Event-log failure must not turn durable acceptance into a false rejection.
        // The same atomic job file retains each notification alongside later state.
        // A bounded backlog permits delivery while the event log is unavailable.
        try
        {
            if (_events.History is null) throw new IOException("Durable event history is required to recover this job notification.");
            foreach (var pending in stored.PendingEvents) _events.PublishJob(pending, stored.Command);
            var cleared = stored with { PendingEvents = null };
            Persist(cleared);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(cleared).Length;
            _jobs[stored.Job.Id] = cleared with { Bytes = bytes };
            _storedBytes += bytes - stored.Bytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { /* The durable job remains authoritative and retains its pending event. */ }
    }

    public void FlushPendingEvents(bool throwOnFailure = true)
    {
        lock (_gate)
        {
            foreach (var stored in _jobs.Values.Where(item => item.PendingEvents?.Length > 0).ToArray())
            {
                Notify(stored);
                if (throwOnFailure && _jobs[stored.Job.Id].PendingEvents?.Length > 0) throw new IOException("Pending job events could not be recovered.");
            }
        }
    }

    private static AcceptedCommand Clone(AcceptedCommand command) => JsonSerializer.Deserialize<AcceptedCommand>(JsonSerializer.SerializeToUtf8Bytes(command))!;

    private static void ValidatePrepared(AcceptedCommand command)
    {
        if (command.Prepared is not { } receipt) return;
        if (receipt.Printer != command.Printer || receipt.ContentHash != PreparedReceiptStore.Hash(receipt.Printer, receipt.WidthMm, receipt.ProfileVersion, receipt.Layout, receipt.Dpi))
            throw new InvalidDataException("Accepted receipt integrity check failed.");
        WindowsTextPrinter.Validate(receipt.Layout);
    }

    private static string Hash(AcceptedCommand command, string? archivedContentHash = null)
    {
        // One 0.0.1 format. Preview IDs/TTLs and transport byte order are not content identity.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { command.Id, command.Type, command.Printer, command.IdempotencyKey,
            command.Html, command.Style, command.ContentJson, contentHash = command.Prepared?.ContentHash ?? archivedContentHash,
            command.Command, command.MetadataJson, command.ReprintOf });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
    private string RecordPath(string id) => Path.Combine(_directory!, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json");

    private void Persist(StoredJob stored)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_directory is null) return;
        var path = RecordPath(stored.Job.Id);
        var temporary = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(stored);
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _owner?.Dispose(); }
    }
    private sealed record StoredJob(int Schema, JobRecord Job, AcceptedCommand Command, string Hash, string? ArchivedContentHash = null, JobRecord[]? PendingEvents = null)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public int Bytes { get; init; }
        [System.Text.Json.Serialization.JsonIgnore]
        public (string? Station, string? OrderID) Metadata { get; } = ReadMetadata(Command.MetadataJson);
        private static (string?, string?) ReadMetadata(string? metadata)
        {
            if (metadata is null) return (null, null);
            using var parsed = JsonDocument.Parse(metadata);
            return (parsed.RootElement.TryGetProperty("station", out var station) ? station.GetString() : null,
                parsed.RootElement.TryGetProperty("orderID", out var order) ? order.GetString() : null);
        }
    }
}
