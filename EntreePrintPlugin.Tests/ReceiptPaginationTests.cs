using System.Drawing;
using System.Text.Json;
using EntreePrintPlugin.Services;
using ZXing;
using ZXing.Common;

namespace EntreePrintPlugin.Tests;

public sealed class ReceiptPaginationTests
{
    [Fact]
    public void LongReceipt_RetainsEveryUnicodeLineExactlyOnceAtOriginalSize()
    {
        var text = Enumerable.Range(0, 40).Select(i => new ReceiptTextRun($"厨房 {i:D2} café", 4, 20 + i * 24,
            130, 14, "Microsoft YaHei", false, false, "#000000")).ToArray();
        var original = new ReceiptTextLayout(272, 970, text, [new(0, 0, 272, 970, "#ffffff"), new(2, 0, 1, 970, "#000000")]);
        var before = JsonSerializer.Serialize(original);
        var pages = ReceiptPaginator.Paginate(original, 272, 200);
        Assert.True(pages.Count >= 5);
        Assert.All(pages, page => Assert.InRange(page.Height, 1, 200));
        var printed = pages.SelectMany(page => page.Text).ToArray();
        Assert.Equal(text.Select(run => run.Text), printed.Select(run => run.Text));
        Assert.All(printed, run => { Assert.Equal(14, run.Size); Assert.Equal(130, run.Width); Assert.Equal("Microsoft YaHei", run.Family); });
        // Background/border clipping conserves their area; input remains immutable for preview/reprints.
        Assert.Equal(original.Rectangles.Sum(box => box.Width * box.Height), pages.Sum(page => page.Rectangles.Sum(box => box.Width * box.Height)), 1);
        Assert.Equal(before, JsonSerializer.Serialize(original));
    }

    [Fact]
    public void BottomAllowanceDoesNotProduceAnExtraBlankSheet()
    {
        var receipt = new ReceiptTextLayout(200, 104, [new("Footer", 0, 90, 40, 10, "Arial", false, false, "#000000")],
            [new(0, 0, 200, 104, "#ffffff")]);
        Assert.Single(ReceiptPaginator.Paginate(receipt, 200, 100));
    }

    [Fact]
    public void OverlappingKeepTogetherSections_MoveTogether()
    {
        var receipt = new ReceiptTextLayout(200, 220, [], [new(0, 80, 10, 100, "#000000")], [new(80, 60), new(130, 50)]);
        var pages = ReceiptPaginator.Paginate(receipt, 200, 100);
        Assert.Equal(80, pages[0].Height);
        Assert.Equal(new[] { new ReceiptKeepTogether(0, 60), new ReceiptKeepTogether(50, 50) }, pages[1].KeepTogether);
    }

    [Theory]
    [InlineData("qrcode", "欢迎 https://entree.example/ticket/123", 203)]
    [InlineData("qrcode", "欢迎 https://entree.example/ticket/123", 300)]
    [InlineData("code128", "ORDER123", 203)]
    [InlineData("code39", "AB-123", 300)]
    public void CodeMovedToNextPage_RetainsQuietZoneAndDecodes(string format, string value, int dpi)
    {
        var code = VectorCodeRenderer.Encode(format, value, availableWidthMm: 68, dpi: dpi);
        var receipt = new ReceiptTextLayout(272, 80 + code.Height + 4, [],
            code.Rectangles.Select(box => box with { X = box.X + 4, Y = box.Y + 80 }).ToArray(),
            [new(80, code.Height)]);
        var pages = ReceiptPaginator.Paginate(receipt, 272, 100);
        Assert.Equal(2, pages.Count);
        Assert.Empty(pages[0].Rectangles);
        Assert.Equal(code.Rectangles.Length, pages[1].Rectangles.Length);
        Assert.InRange(Assert.Single(pages[1].KeepTogether!).Y, 0, 96f / dpi);
        using var bitmap = new Bitmap((int)Math.Ceiling(272 * dpi / 96f), (int)Math.Ceiling(100 * dpi / 96f));
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.ScaleTransform(dpi / 96f, dpi / 96f);
            WindowsTextPrinter.Draw(graphics, pages[1]);
        }
        var pixels = new byte[bitmap.Width * bitmap.Height * 3];
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            var index = (y * bitmap.Width + x) * 3;
            pixels[index] = color.R; pixels[index + 1] = color.G; pixels[index + 2] = color.B;
        }
        var decoded = new BarcodeReaderGeneric { Options = new DecodingOptions { TryHarder = true } }
            .Decode(new RGBLuminanceSource(pixels, bitmap.Width, bitmap.Height, RGBLuminanceSource.BitmapFormat.RGB24));
        Assert.Equal(value, decoded?.Text);
    }

    [Fact]
    public void OversizeSectionFailsBeforeAnyPagesAreReturned()
    {
        var receipt = new ReceiptTextLayout(200, 400, [], [], [new(200, 150)]);
        Assert.Equal("LAYOUT_ITEM_TOO_TALL", Assert.Throws<CommandException>(() => ReceiptPaginator.Paginate(receipt, 200, 100)).Code);
        Assert.Equal("LAYOUT_OVERFLOW", Assert.Throws<CommandException>(() => ReceiptPaginator.Paginate(receipt, 180, 400)).Code);
    }

    [Theory]
    [InlineData(float.NaN, 10)]
    [InlineData(10, float.PositiveInfinity)]
    [InlineData(99, 10)]
    public void InvalidSavedPageBoundariesAreRejected(float y, float height)
    {
        Assert.Throws<ArgumentException>(() => WindowsTextPrinter.Validate(new(200, 100, [], [], [new(y, height)])));
    }

    [Fact]
    public async Task ChromiumPreparationAndJsonRoundTrip_PreserveCodeAndCssPageBoundaries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EntreePaginationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var html = "<body style='margin:0'><div style='height:70px'>Header</div>" +
                VectorCodeRenderer.ToHtml(VectorCodeRenderer.Encode("qrcode", "ENTREE123"), "ENTREE123", showText: true) +
                "<div style='break-inside:avoid;height:40px'>厨房 footer</div></body>";
            var original = await ReceiptLayoutEngine.PrepareAsync(html, 72, directory, CancellationToken.None);
            var saved = JsonSerializer.Deserialize<ReceiptTextLayout>(JsonSerializer.Serialize(original))!;
            Assert.Contains(saved.KeepTogether!, region => region.Y == 70 && region.Height > 90);
            Assert.Contains(saved.KeepTogether!, region => region.Height == 40);
            var pages = ReceiptPaginator.Paginate(saved, saved.Width, 150);
            Assert.True(pages.Count > 1);
            var codePage = Assert.Single(pages, page => page.Rectangles.Any(box => box.Color == "#000000"));
            Assert.Contains(codePage.Text, run => run.Text == "ENTREE123"); // Barcode label travels with the code.
            Assert.Equal(original.Text.Select(run => run.Text), pages.SelectMany(page => page.Text).Select(run => run.Text));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
