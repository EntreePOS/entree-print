using System.Drawing;
using System.Drawing.Imaging;
using EntreePrintPlugin.Services;

namespace EntreePrintPlugin.Tests;

public class WindowsTextPrinterTests
{
    private static ReceiptTextLayout Sample() => new(272.126f, 100,
        [new("欢迎 · 歡迎 · café & <tea>", 10, 30, 220, 13.333f, "Microsoft YaHei", false, false, "#000000")],
        [new(10, 40, 200, 1, "#000000")]);

    [Fact]
    public void MetafileRetainsTextAndDoesNotContainImageDrawing()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".emf");
        try
        {
            WindowsTextPrinter.WriteMetafile(path, Sample());
            using var metafile = new Metafile(path);
            using var graphics = Graphics.FromHwnd(IntPtr.Zero);
            var records = new List<EmfPlusRecordType>();
            graphics.EnumerateMetafile(metafile, Point.Empty, (type, flags, size, data, callback) => { records.Add(type); return true; });
            Assert.Contains(EmfPlusRecordType.DrawString, records);
            Assert.DoesNotContain(EmfPlusRecordType.DrawImage, records);
            Assert.DoesNotContain(EmfPlusRecordType.DrawImagePoints, records);
            Assert.DoesNotContain(EmfPlusRecordType.EmfStretchDIBits, records);
            Assert.Contains(EmfPlusRecordType.FillRects, records);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ChineseUsesAnExplicitFontFromTheCssFallbackStack()
    {
        var layout = Sample();
        layout = layout with { Text = [layout.Text[0] with { Family = "Arial, 'Microsoft YaHei', sans-serif" }] };
        var resolved = WindowsTextPrinter.ResolveFonts(layout);
        Assert.Equal("Microsoft YaHei", resolved.Text[0].Family);
    }

    [Fact]
    public void PreviewPreservesUnicodeAndEscapesMarkup()
    {
        var svg = WindowsTextPrinter.ToSvg(Sample());
        Assert.Contains("<text", svg);
        Assert.Contains("&amp;", svg);
        Assert.Contains("&lt;tea&gt;", svg);
        Assert.DoesNotContain("<image", svg);
        Assert.Contains("textLength=\"220\"", svg);
    }

    [Fact]
    public void OverflowFailsInsteadOfSilentlyScalingReceipt()
    {
        var receipt = Sample();
        Assert.Throws<ArgumentException>(() => WindowsTextPrinter.Validate(receipt with { Width = 200 }));
        Assert.Throws<ArgumentException>(() => WindowsTextPrinter.Validate(receipt with { Height = float.NaN }));
    }
}
