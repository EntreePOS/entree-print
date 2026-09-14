using System.Text.Json;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;

namespace EntreePrintPlugin.Tests;

public sealed class PreparedReceiptTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EntreePreparedTests", Guid.NewGuid().ToString("N"));
    private static ReceiptTextLayout Layout(string text = "中文 receipt") => new(272, 60, [new(text, 0, 20, 120, 16, "Arial", false, false, "#000")], []);

    [Fact]
    public void ComparisonEvidenceSurvivesRestartAndCannotBeChangedInStoredRecords()
    {
        var directory = Path.Combine(_directory, "renders");
        var identity = new ServiceIdentity(_directory);
        var store = new PreparedReceiptStore(directory, identity);
        var comparison = "layout:" + new string('a', 64);
        var receipt = store.Save("Kitchen", 72, "profile", Layout(), 203, comparison);
        var reopened = new PreparedReceiptStore(directory, identity).Get(receipt.Id);
        Assert.Equal(comparison, reopened.ComparisonId);
        var view = JsonSerializer.SerializeToElement(PreparedReceiptStore.View(reopened));
        Assert.Equal(comparison, view.GetProperty("ComparisonId").GetString());
        Assert.Equal(0, view.GetProperty("warnings").GetArrayLength());
        var jobDirectory = Path.Combine(_directory, "jobs");
        using (var jobs = new JobStore(new EventBroadcaster(), jobDirectory))
            jobs.Accept(new AcceptedCommand("id", "print", "Kitchen", "", "", "", null, receipt, "intent"), 1);
        using (var jobs = new JobStore(new EventBroadcaster(), jobDirectory))
            Assert.Equal(comparison, jobs.GetCommand("id")!.Prepared!.ComparisonId);
        foreach (var path in new[] { Path.Combine(directory, receipt.Id + ".json"), Directory.GetFiles(jobDirectory, "*.json").Single() })
            File.WriteAllText(path, File.ReadAllText(path).Replace(comparison, "layout:" + new string('b', 64)));
        Assert.Throws<InvalidDataException>(() => store.Get(receipt.Id));
        Assert.Throws<InvalidDataException>(() => new JobStore(new EventBroadcaster(), jobDirectory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("layout:abc")]
    [InlineData("layout:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void InvalidComparisonIdsAreRejected(string comparison)
    {
        var store = new PreparedReceiptStore(Path.Combine(_directory, "renders"), new ServiceIdentity(_directory));
        Assert.Throws<ArgumentException>(() => store.Save("Kitchen", 72, "profile", Layout(), 203, comparison));
    }

    [Fact]
    public void UnavailableComparisonKeepsPreviewAvailableWithAWarning()
    {
        var store = new PreparedReceiptStore(Path.Combine(_directory, "renders"), new ServiceIdentity(_directory));
        var receipt = store.Save("Kitchen", 72, "profile", Layout());
        var view = JsonSerializer.SerializeToElement(PreparedReceiptStore.View(receipt));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("ComparisonId").ValueKind);
        Assert.Equal(1, view.GetProperty("warnings").GetArrayLength());
        Assert.Contains("<svg", view.GetProperty("html").GetString());
    }

    [Fact]
    public void PreviewExpiry_DoesNotEraseAcceptedArtifactOrPermitChangedReplay()
    {
        var clock = new TestClock();
        var identity = new ServiceIdentity(_directory);
        var store = new PreparedReceiptStore(Path.Combine(_directory, "renders"), identity, clock);
        var preview = store.Save("Kitchen", 72, "profile", Layout());
        using var jobs = new JobStore(new EventBroadcaster(), Path.Combine(_directory, "jobs"));
        var command = new AcceptedCommand("v2-intent", "print", "Kitchen", "", "", "", null, preview, "intent", "{}", "first-digest");
        jobs.Accept(command, 2);
        // The caller cannot modify a durably accepted receipt through an array reference.
        preview.Layout.Text[0] = preview.Layout.Text[0] with { Text = "modified outside store" };
        Assert.Equal("中文 receipt", jobs.GetCommand(command.Id)!.Prepared!.Layout.Text[0].Text);
        clock.Now += TimeSpan.FromHours(1);
        Assert.Equal("RENDER_EXPIRED", Assert.Throws<CommandException>(() => store.Get(preview.Id)).Code);
        var accepted = jobs.GetCommand(command.Id)!;
        Assert.False(jobs.Accept(accepted with { RequestDigest = "different bytes, same meaning" }, 2).Created);
        var other = store.Save("Kitchen", 72, "profile", Layout("changed"));
        Assert.Equal("IDEMPOTENCY_CONFLICT", Assert.Throws<CommandException>(() => jobs.Accept(accepted with { Prepared = other }, 2)).Code);
        var view = JsonSerializer.SerializeToElement(PreparedReceiptStore.View(accepted.Prepared!));
        Assert.Contains("中文 receipt", System.Net.WebUtility.HtmlDecode(view.GetProperty("html").GetString()!));
    }

    [Fact]
    public void IdenticalLayoutsWithDifferentPreviewIds_ResolveSameIntent()
    {
        var store = new PreparedReceiptStore(Path.Combine(_directory, "renders"), new ServiceIdentity(_directory));
        var first = store.Save("Kitchen", 72, "profile", Layout());
        var second = store.Save("Kitchen", 72, "profile", Layout());
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.ContentHash, second.ContentHash);
        using var jobs = new JobStore(new EventBroadcaster());
        var command = new AcceptedCommand("id", "print", "Kitchen", "", "", "", null, first, "intent");
        jobs.Accept(command, 2);
        Assert.False(jobs.Accept(command with { Prepared = second }, 2).Created);
    }

    [Fact]
    public void StoredLayoutTampering_FailsIntegrityCheck()
    {
        var renderDirectory = Path.Combine(_directory, "renders");
        var store = new PreparedReceiptStore(renderDirectory, new ServiceIdentity(_directory));
        var preview = store.Save("Kitchen", 72, "profile", Layout());
        var path = Path.Combine(renderDirectory, preview.Id + ".json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("receipt", "changed"));
        Assert.Throws<InvalidDataException>(() => store.Get(preview.Id));
    }

    [Fact]
    public void AcceptedLayoutTampering_IsRejectedOnRestart()
    {
        var renders = new PreparedReceiptStore(Path.Combine(_directory, "renders"), new ServiceIdentity(_directory));
        var receipt = renders.Save("Kitchen", 72, "profile", Layout());
        var jobDirectory = Path.Combine(_directory, "jobs");
        using (var jobs = new JobStore(new EventBroadcaster(), jobDirectory))
            jobs.Accept(new AcceptedCommand("id", "print", "Kitchen", "", "", "", null, receipt, "intent"), 1);
        var path = Directory.GetFiles(jobDirectory, "*.json").Single();
        File.WriteAllText(path, File.ReadAllText(path).Replace("receipt", "altered"));
        Assert.Throws<InvalidDataException>(() => new JobStore(new EventBroadcaster(), jobDirectory));
    }

    [Fact]
    public void Blocks_RequireDpiForCodes_AndRejectImagesAndUnknownFields()
    {
        using var qr = JsonDocument.Parse("""[{"type":"qrcode","value":"中文"}]""");
        Assert.Equal("PRINTER_SETTINGS_UNSUPPORTED", Assert.Throws<CommandException>(() => V2ApiService.BuildBlocks(qr.RootElement, 72, null)).Code);
        Assert.Contains("position:absolute", V2ApiService.BuildBlocks(qr.RootElement, 72, 203));
        using var image = JsonDocument.Parse("""[{"type":"image","url":"https://example.com/logo.png"}]""");
        Assert.Equal("CONTENT_TYPE_UNSUPPORTED", Assert.Throws<CommandException>(() => V2ApiService.BuildBlocks(image.RootElement, 72, 203)).Code);
        using var extra = JsonDocument.Parse("""[{"type":"html","html":"<p>hello</p>","cut":true}]""");
        Assert.Equal("FIELD_UNSUPPORTED", Assert.Throws<CommandException>(() => V2ApiService.BuildBlocks(extra.RootElement, 72, 203)).Code);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
