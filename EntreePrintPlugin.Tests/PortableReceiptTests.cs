using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;
using static EntreePrintPlugin.Tests.V2EndpointTests;

namespace EntreePrintPlugin.Tests;

public sealed class PortableReceiptTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static void Network(Harness host, string name = "Kitchen") => host.Inventory.Items =
        [host.Inventory.Items[0] with { Name = name, QueueType = 0, PortName = "IP_fixture", PortMonitor = "TCPMON.DLL",
            PortProtocol = 1, PortNumber = 9100, HostAddress = "192.168.1.50", Stale = false }];

    private static JsonObject Artifact(Harness host)
    {
        var printer = host.Inventory.Items[0];
        var layout = host.Preview.Layout with { Width = (float)(72m * 96m / 25.4m) };
        var comparison = ReceiptComparison.Create(layout, printer.DriverName, host.Driver.Layout, CancellationToken.None)!;
        return JsonSerializer.SerializeToNode(new PortableReceipt(1, NetworkPrinterDestination.From(printer)!.Id,
            comparison, 72, 203, host.Clock.Now.AddMinutes(10), layout), Web)!.AsObject();
    }

    private static async Task<HttpResponseMessage> Render(Harness host, object body)
    {
        using var request = host.Request(JsonSerializer.Serialize(body, Web));
        request.RequestUri = new Uri("/api/renders", UriKind.Relative);
        return await host.Client.SendAsync(request);
    }

    [Fact]
    public async Task TransfersReviewedChineseAndCodesAcrossServicesWithoutRerenderingOrSubmitting()
    {
        await using var source = await Harness.Start();
        await using var backup = await Harness.Start(new PluginSettings { AccessToken = new string('t', 32),
            BrowserExecutablePath = @"C:\missing-fixture-browser\browser.exe" });
        Network(source); Network(backup, "Renamed kitchen");
        using var firstResponse = await Render(source, new { printer = "Kitchen", content = new object[] {
            new { type = "html", html = "<p style='margin:0;font:16px Arial,Microsoft YaHei'>欢迎 café</p>" },
            new { type = "qrcode", value = "ENTREE-PORTABLE-001" },
            new { type = "barcode", format = "code128", value = "PORTABLE001" }
        } });
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = JsonNode.Parse(await firstResponse.Content.ReadAsStringAsync())!;
        Assert.NotNull(first["artifact"]);
        using var importedResponse = await Render(backup, new { printer = "Renamed kitchen", artifact = first["artifact"] });
        Assert.Equal(HttpStatusCode.OK, importedResponse.StatusCode);
        var imported = JsonNode.Parse(await importedResponse.Content.ReadAsStringAsync())!;
        Assert.Equal(first["html"]!.GetValue<string>(), imported["html"]!.GetValue<string>());
        Assert.Equal(first["comparisonId"]!.GetValue<string>(), imported["comparisonId"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(first["artifact"], imported["artifact"]));
        Assert.NotEqual(first["id"]!.GetValue<string>(), imported["id"]!.GetValue<string>());
        Assert.Equal(backup.Identity.ServiceId, imported["serviceId"]!.GetValue<string>());
        var store = new PreparedReceiptStore(Path.Combine(backup.DirectoryPath, "renders"), backup.Identity, backup.Clock);
        var saved = store.Get(imported["id"]!.GetValue<string>());
        Assert.Equal(first["expiresAt"]!.GetValue<DateTimeOffset>(), saved.ExpiresAt);
        Assert.Contains(saved.Layout.Text, run => run.Text.Contains("欢迎"));
        Assert.Contains(saved.Layout.Rectangles, box => box.DotDpi == 203);
        Assert.Empty(source.Jobs.List()); Assert.Empty(backup.Jobs.List());
        Assert.Equal(0, source.Backend.Calls); Assert.Equal(0, backup.Backend.Calls);
    }

    [Theory]
    [InlineData("usb", "RENDER_DESTINATION_MISMATCH")]
    [InlineData("stale", "RENDER_DESTINATION_MISMATCH")]
    [InlineData("destination", "RENDER_DESTINATION_MISMATCH")]
    [InlineData("driver", "RENDER_CHANGED")]
    [InlineData("dpi", "RENDER_CHANGED")]
    [InlineData("offset", "RENDER_CHANGED")]
    [InlineData("text", "RENDER_CHANGED")]
    [InlineData("geometry", "RENDER_CHANGED")]
    [InlineData("expired", "RENDER_EXPIRED")]
    public async Task RejectsIneligibleOrChangedArtifactsWithoutSavingOrAccepting(string change, string code)
    {
        await using var host = await Harness.Start(); Network(host);
        var artifact = Artifact(host);
        switch (change)
        {
            case "usb": host.Inventory.Items = [host.Inventory.Items[0] with { PortName = "USB001" }]; break;
            case "stale": host.Inventory.Items = [host.Inventory.Items[0] with { Stale = true }]; break;
            case "destination": host.Inventory.Items = [host.Inventory.Items[0] with { HostAddress = "192.168.1.51" }]; break;
            case "driver": host.Inventory.Items = [host.Inventory.Items[0] with { DriverName = "different" }]; break;
            case "dpi": host.Driver.Layout = host.Driver.Layout with { DpiX = 300 }; break;
            case "offset": host.Driver.Layout = host.Driver.Layout with { OffsetXDots = 33 }; break;
            case "text": artifact["layout"]!["text"]![0]!["text"] = "changed receipt"; break;
            case "geometry": artifact["layout"]!["text"]![0]!["x"] = 1; break;
            case "expired": artifact["expiresAt"] = host.Clock.Now.AddSeconds(-1); break;
        }
        var before = Directory.GetFiles(Path.Combine(host.DirectoryPath, "renders"));
        using var response = await Render(host, new { printer = "Kitchen", artifact });
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!;
        Assert.Equal(code, error["code"]!.GetValue<string>());
        Assert.Equal("not_sent", error["delivery"]!.GetValue<string>());
        Assert.Equal(before, Directory.GetFiles(Path.Combine(host.DirectoryPath, "renders")));
        Assert.Empty(host.Jobs.List()); Assert.Equal(0, host.Backend.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("nestedUnknown")]
    [InlineData("nullText")]
    [InlineData("nullRun")]
    [InlineData("width")]
    [InlineData("version")]
    public async Task RejectsMalformedTransferData(string change)
    {
        await using var host = await Harness.Start(); Network(host);
        JsonObject? artifact = Artifact(host);
        switch (change)
        {
            case "null": artifact = null; break;
            case "missing": artifact.Remove("comparisonId"); break;
            case "unknown": artifact["printer"] = "other"; break;
            case "nestedUnknown": artifact["layout"]!["text"]![0]!["command"] = "cut"; break;
            case "nullText": artifact["layout"]!["text"] = null; break;
            case "nullRun": artifact["layout"]!["text"]![0] = null; break;
            case "width": artifact["widthMm"] = 60; break;
            case "version": artifact["version"] = 2; break;
        }
        using var response = await Render(host, new { printer = "Kitchen", artifact });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("ARTIFACT_INVALID", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["code"]!.GetValue<string>());
        Assert.Empty(host.Jobs.List()); Assert.Equal(0, host.Backend.Calls);
    }

    [Fact]
    public async Task ImportCannotOverrideLayoutAndRequiresAuthenticatedChecksummedRequest()
    {
        await using var host = await Harness.Start(); Network(host);
        var artifact = Artifact(host);
        using var combined = await Render(host, new { printer = "Kitchen", artifact, html = "replacement" });
        Assert.Equal("FIELD_UNSUPPORTED", JsonNode.Parse(await combined.Content.ReadAsStringAsync())!["error"]!["code"]!.GetValue<string>());
        foreach (var remove in new[] { "Authorization", V2Request.DigestHeader, "X-Entree-Service-ID" })
        {
            using var request = host.Request(JsonSerializer.Serialize(new { printer = "Kitchen", artifact }, Web));
            request.RequestUri = new Uri("/api/renders", UriKind.Relative);
            if (remove == "Authorization") request.Headers.Authorization = new("Bearer", "wrong");
            else request.Headers.Remove(remove);
            using var response = await host.Client.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode);
        }
        Assert.Empty(host.Jobs.List()); Assert.Equal(0, host.Backend.Calls);
    }

    [Fact]
    public async Task ImportedDestinationChangeIsRejectedAtNewAcceptance()
    {
        await using var host = await Harness.Start(); Network(host);
        using var response = await Render(host, new { printer = "Kitchen", artifact = Artifact(host) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
        var store = new PreparedReceiptStore(Path.Combine(host.DirectoryPath, "renders"), host.Identity, host.Clock);
        host.Preview = store.Get(id);
        host.Inventory.Items = [host.Inventory.Items[0] with { HostAddress = "192.168.1.51" }];
        using var changed = await host.Client.SendAsync(host.Request(host.PrintBody("imported")));
        Assert.Equal("PRINTER_SETTINGS_CHANGED", JsonNode.Parse(await changed.Content.ReadAsStringAsync())!["error"]!["code"]!.GetValue<string>());
        Assert.Empty(host.Jobs.List()); Assert.Equal(0, host.Backend.Calls);
    }

    [Fact]
    public async Task UncomparableOrOversizedReceiptsKeepOrdinaryPreviewsWithoutPortableData()
    {
        await using var host = await Harness.Start();
        Assert.Null(PortableReceipt.Create(host.Preview, host.Inventory.Items[0]));
        Network(host);
        Assert.Null(PortableReceipt.Create(host.Preview, host.Inventory.Items[0]));
        var receipt = host.Preview with { Dpi = 203, ComparisonId = "layout:" + new string('a', 64) };
        Assert.NotNull(PortableReceipt.Create(receipt, host.Inventory.Items[0]));
        var large = receipt with { Layout = receipt.Layout with {
            Text = [receipt.Layout.Text[0] with { Text = new string('a', 900_000) }] } };
        Assert.Null(PortableReceipt.Create(large, host.Inventory.Items[0]));
        Assert.NotNull(PreparedReceiptStore.View(large));
    }

    [Fact]
    public async Task TransferExpiryCannotBeExtendedBySavingOrRepeatedImport()
    {
        await using var host = await Harness.Start(); Network(host);
        var artifact = Artifact(host);
        var expiry = artifact["expiresAt"]!.GetValue<DateTimeOffset>();
        host.Clock.Now = host.Clock.Now.AddMinutes(2);
        using var firstResponse = await Render(host, new { printer = "Kitchen", artifact });
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = JsonNode.Parse(await firstResponse.Content.ReadAsStringAsync())!;
        host.Clock.Now = host.Clock.Now.AddMinutes(2);
        using var nextResponse = await Render(host, new { printer = "Kitchen", artifact = first["artifact"] });
        Assert.Equal(HttpStatusCode.OK, nextResponse.StatusCode);
        var next = JsonNode.Parse(await nextResponse.Content.ReadAsStringAsync())!;
        Assert.Equal(expiry, next["expiresAt"]!.GetValue<DateTimeOffset>());
        host.Clock.Now = expiry;
        using var expired = await Render(host, new { printer = "Kitchen", artifact = next["artifact"] });
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Empty(host.Jobs.List()); Assert.Equal(0, host.Backend.Calls);
    }
}
