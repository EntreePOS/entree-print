using EntreePrintPlugin.Services;

namespace EntreePrintPlugin.Tests;

public sealed class CodeDotAlignmentTests
{
    [Theory]
    [InlineData("qrcode", "right", 203)]
    [InlineData("qrcode", "center", 300)]
    [InlineData("qrcode", "left", 600)]
    [InlineData("code128", "center", 203)]
    [InlineData("code128", "right", 300)]
    [InlineData("code128", "center", 600)]
    public async Task FractionalHtmlPlacement_StaysOnPrinterDotsInPreviewAndAfterPageBreak(string format, string align, int dpi)
    {
        var directory = Path.Combine(Path.GetTempPath(), "EntreeDotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var code = VectorCodeRenderer.Encode(format, "ORDER123", availableWidthMm: 68, dpi: dpi);
            var html = "<body style='margin:0;padding-left:0.31px'><div style='height:139.37px'>Header</div>" +
                VectorCodeRenderer.ToHtml(code, "ORDER123", align, showText: format != "qrcode") + "</body>";
            var layout = await ReceiptLayoutEngine.PrepareAsync(html, 72, directory, CancellationToken.None);
            Assert.Empty(Directory.EnumerateDirectories(directory, "text-browser-*"));
            AssertGrid(layout, dpi);
            var savedBoxes = layout.Rectangles.Where(box => box.DotDpi.HasValue).ToArray();
            Assert.Equal(code.Rectangles.Length, savedBoxes.Length);
            for (var index = 0; index < savedBoxes.Length; index++)
            {
                Assert.Equal(MathF.Round(code.Rectangles[index].Width * dpi / 96), MathF.Round(savedBoxes[index].Width * dpi / 96));
                Assert.Equal(MathF.Round(code.Rectangles[index].Height * dpi / 96), MathF.Round(savedBoxes[index].Height * dpi / 96));
            }
            var pages = ReceiptPaginator.Paginate(layout, layout.Width, 160, dpi);
            Assert.True(pages.Count > 1);
            Assert.Equal(layout.Rectangles.Count(box => box.DotDpi.HasValue), pages.Sum(page => page.Rectangles.Count(box => box.DotDpi.HasValue)));
            Assert.Single(pages, page => page.Rectangles.Any(box => box.DotDpi.HasValue));
            foreach (var page in pages) AssertGrid(page, dpi);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void TextAndOrdinaryGraphicsAreNotSnappedOrMutated()
    {
        var layout = new ReceiptTextLayout(200, 100, [new("Header", 0.31f, 15.27f, 60.1f, 12, "Arial", false, false, "#000000")],
            [new(0.31f, 21.27f, 20.19f, 5.51f, "#000000"), new(30.31f, 21.27f, 20.19f, 5.51f, "#000000", 203)]);
        var snapped = VectorCodeRenderer.AlignToDots(layout);
        Assert.Equal(layout.Text, snapped.Text);
        Assert.Equal(layout.Rectangles[0], snapped.Rectangles[0]);
        Assert.NotEqual(layout.Rectangles[1], snapped.Rectangles[1]);
        Assert.Equal(30.31f, layout.Rectangles[1].X);
        AssertGrid(snapped, 203);
    }

    [Fact]
    public void PreparedCodeCannotBePrintedAtAnotherDriverResolution()
    {
        var code = VectorCodeRenderer.Encode("qrcode", "ORDER123", dpi: 203);
        var layout = new ReceiptTextLayout(code.Width, code.Height, [], code.Rectangles);
        var error = Assert.Throws<CommandException>(() => ReceiptPaginator.Paginate(layout, 300, 300, 300));
        Assert.Equal("PRINTER_SETTINGS_CHANGED", error.Code);
    }

    private static void AssertGrid(ReceiptTextLayout layout, int dpi)
    {
        foreach (var box in layout.Rectangles.Where(box => box.DotDpi.HasValue))
        {
            Assert.Equal(dpi, box.DotDpi);
            foreach (var edge in new[] { box.X, box.Y, box.X + box.Width, box.Y + box.Height })
            {
                var dots = edge * dpi / 96;
                Assert.InRange(Math.Abs(dots - MathF.Round(dots)), 0, 0.002f);
            }
            Assert.True(box.Width > 0);
            Assert.True(box.Height > 0);
        }
    }
}
