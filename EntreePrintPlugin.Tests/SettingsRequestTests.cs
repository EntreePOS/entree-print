using System.Text;
using System.Text.Json;
using EntreePrintTray;

namespace EntreePrintPlugin.Tests;

public sealed class SettingsRequestTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "EntreeSettingsRequestTests", Guid.NewGuid().ToString("N"));
    private string Request => Path.Combine(directory, "request.json");

    public SettingsRequestTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void ValidRequestPreservesUnicodeAndServiceOnlySettings()
    {
        File.WriteAllText(Request, "{\"HttpPort\":19779,\"BrowserExecutablePath\":\"C:\\\\浏览器\\\\chrome.exe\",\"SpoolDirectory\":\"厨房\"}", new UTF8Encoding(false));
        var config = SettingsWriter.ReadRequest(Request);
        Assert.Equal(19779, config.HttpPort);
        Assert.Equal(@"C:\浏览器\chrome.exe", config.BrowserExecutablePath);
        Assert.Equal("厨房", config.AdditionalSettings!["SpoolDirectory"].GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"HttpPort\":")]
    public void InvalidRequestCannotBecomeDefaultSettings(string json)
    {
        File.WriteAllText(Request, json);
        Assert.Throws<JsonException>(() => SettingsWriter.ReadRequest(Request));
    }

    [Fact]
    public void MissingRequestCannotBecomeDefaultSettings() =>
        Assert.Throws<FileNotFoundException>(() => SettingsWriter.ReadRequest(Request));

    [Fact]
    public void InvalidSettingsAreRejectedBeforeSaving()
    {
        File.WriteAllText(Request, "{\"HttpPort\":-1}");
        Assert.Throws<ArgumentException>(() => SettingsWriter.ReadRequest(Request));
    }

    [Fact]
    public void ExactByteLimitIsAcceptedButOneExtraByteIsRejected()
    {
        File.WriteAllText(Request, "{}" + new string(' ', SettingsWriter.MaximumRequestBytes - 2), new UTF8Encoding(false));
        Assert.Equal(9779, SettingsWriter.ReadRequest(Request).HttpPort);
        File.AppendAllText(Request, " ");
        Assert.Throws<ArgumentException>(() => SettingsWriter.ReadRequest(Request));
    }

    [Fact]
    public void ConcurrentWriterPreventsReadingAnUnstableRequest()
    {
        File.WriteAllText(Request, "{}");
        using var writer = new FileStream(Request, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        Assert.Throws<IOException>(() => SettingsWriter.ReadRequest(Request));
    }

    [Fact]
    public void GrowthAndPartialReadsCannotBypassTheByteLimit()
    {
        using var stream = new GrowingStream();
        Assert.Throws<ArgumentException>(() => SettingsWriter.ReadRequest(stream));
        Assert.Equal(SettingsWriter.MaximumRequestBytes + 1, stream.BytesRead);
    }

    [Fact]
    public async Task OversizeSaveLeavesTheExistingConfigurationUntouched()
    {
        File.WriteAllText(Request, "existing settings");
        var config = new PluginConfig { BrowserExecutablePath = new string('x', SettingsWriter.MaximumRequestBytes) };
        await Assert.ThrowsAsync<ArgumentException>(() => SettingsWriter.SaveAsync(config, Request));
        Assert.Equal("existing settings", File.ReadAllText(Request));
    }

    private sealed class GrowingStream : MemoryStream
    {
        public int BytesRead { get; private set; }
        public override long Length => 2; // The request grew after this observation.
        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, 4093);
            buffer[..count].Fill((byte)' ');
            BytesRead += count;
            return count;
        }
    }
}
