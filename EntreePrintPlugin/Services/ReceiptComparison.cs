using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace EntreePrintPlugin.Services;

// Comparison evidence only: this does not transfer artifacts or authorize a job.
internal static class ReceiptComparison
{
    internal static string? Create(ReceiptTextLayout layout, string driverName, PrinterLayoutSettings driver,
        CancellationToken token, Func<ReceiptTextRun, string?>? readFont = null)
    {
        WindowsTextPrinter.Validate(layout);
        var fonts = layout.Text.GroupBy(run => run with { Text = "", X = 0, Baseline = 0, Width = 1, Color = "" })
            .Select(group => group.Key with { Text = string.Concat(group.Select(run => run.Text)) })
            .OrderBy(run => run.Family, StringComparer.Ordinal).ThenBy(run => run.Bold)
            .ThenBy(run => run.Italic).ThenBy(run => run.Size).ToArray();
        if (fonts.Length > 64) return null;
        var hashes = new List<string>();
        var elapsed = Stopwatch.StartNew();
        long remainingBytes = 256_000_000;
        foreach (var font in fonts)
        {
            token.ThrowIfCancellationRequested();
            if (elapsed.Elapsed > TimeSpan.FromSeconds(3)) return null;
            var hash = readFont is not null ? readFont(font) : FontHash(font, token, elapsed, ref remainingBytes);
            if (hash is null) return null;
            hashes.Add(hash);
        }
        token.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "receipt-comparison-v1", renderer = typeof(WindowsTextPrinter).Assembly.ManifestModule.ModuleVersionId,
            windows = Environment.OSVersion.Version.ToString(), driverName, driver, layout, fonts = hashes
        });
        return "layout:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string? FontHash(ReceiptTextRun run, CancellationToken token, Stopwatch elapsed, ref long remainingBytes)
    {
        try
        {
            var style = (run.Bold ? FontStyle.Bold : FontStyle.Regular) | (run.Italic ? FontStyle.Italic : FontStyle.Regular);
            using var font = new Font(run.Family, run.Size, style, GraphicsUnit.Pixel);
            if (!font.FontFamily.Name.Equals(run.Family, StringComparison.OrdinalIgnoreCase)) return null;
            using var graphics = Graphics.FromHwnd(IntPtr.Zero);
            var dc = graphics.GetHdc();
            try
            {
                var handle = font.ToHfont();
                var previous = SelectObject(dc, handle);
                try
                {
                    if (previous == IntPtr.Zero || previous == new IntPtr(-1)) return null;
                    // Do not certify a font while printing could silently use fallback glyphs.
                    // Supplementary characters need a shaping-aware coverage check; decline
                    // comparison conservatively until that is implemented.
                    if (run.Text.Any(char.IsSurrogate)) return null;
                    var glyphs = new ushort[run.Text.Length];
                    if (GetGlyphIndices(dc, run.Text, run.Text.Length, glyphs, 1) != (uint)run.Text.Length ||
                        glyphs.Any(glyph => glyph == ushort.MaxValue)) return null;
                    // Table 0 covers the selected TrueType face, including a TTC face.
                    // Hash locally in chunks; no font bytes are stored or transmitted.
                    var size = GetFontData(dc, 0, 0, null, 0);
                    if (size is 0 or uint.MaxValue || size > 64_000_000 || size > remainingBytes) return null;
                    remainingBytes -= size;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[65536];
                    for (uint offset = 0; offset < size;)
                    {
                        token.ThrowIfCancellationRequested();
                        if (elapsed.Elapsed > TimeSpan.FromSeconds(3)) return null;
                        var count = Math.Min((uint)buffer.Length, size - offset);
                        if (GetFontData(dc, 0, offset, buffer, count) != count) return null;
                        hash.AppendData(buffer, 0, (int)count); offset += count;
                    }
                    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }
                finally
                {
                    if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
                    DeleteObject(handle);
                }
            }
            finally { graphics.ReleaseHdc(dc); }
        }
        catch (Exception error) when (error is ArgumentException or ExternalException)
        { return null; }
    }

    [DllImport("gdi32.dll")] private static extern uint GetFontData(IntPtr dc, uint table, uint offset, [Out] byte[]? buffer, uint length);
    [DllImport("gdi32.dll", EntryPoint = "GetGlyphIndicesW", CharSet = CharSet.Unicode)]
    private static extern uint GetGlyphIndices(IntPtr dc, string text, int count, [Out] ushort[] glyphs, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
}
