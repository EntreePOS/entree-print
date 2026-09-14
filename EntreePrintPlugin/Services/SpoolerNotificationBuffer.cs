namespace EntreePrintPlugin.Services;

// Native batches can interleave document and status fields, including reused numeric job IDs.
internal sealed class SpoolerNotificationBuffer
{
    internal sealed record Entry(uint JobId, string? Document = null, uint? Flags = null);
    internal sealed record Observation(string? Document, uint? Flags);
    private readonly Dictionary<uint, Observation> _observed = new();

    public Observation? Get(uint id) => _observed.GetValueOrDefault(id);
    public void Clear() => _observed.Clear();

    public void Apply(IEnumerable<Entry> entries)
    {
        foreach (var group in entries.GroupBy(entry => entry.JobId))
        {
            var prior = Get(group.Key) ?? new Observation(null, null);
            var documents = group.Where(entry => entry.Document is not null).Select(entry => entry.Document!).Distinct().ToArray();
            if (documents.Length > 1) { _observed.Remove(group.Key); continue; } // Ambiguous identity: await a fresh query.
            var document = documents.SingleOrDefault() ?? prior.Document;
            var changed = prior.Document is not null && document != prior.Document;
            uint? flags = changed ? null : prior.Flags;
            if (!changed)
                foreach (var entry in group.Where(entry => entry.Flags.HasValue))
                    flags = entry.Flags!.Value | ((flags ?? 0) & 0x1080);
            // On reuse, discard even same-batch status: it may belong to the previous document.
            _observed[group.Key] = new(document, flags);
        }
        if (_observed.Count > 4096) Clear(); // Missing evidence never implies success.
    }
}
