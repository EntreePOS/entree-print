using System.Drawing;
using EntreePrintPlugin.Services;
using ZXing;
using ZXing.Common;

namespace EntreePrintPlugin.Tests;

public sealed class VectorCodeTests
{
    [Theory]
    [InlineData("qrcode", "欢迎 歡迎 café https://entree.example/10086", 203)]
    [InlineData("qrcode", "欢迎 歡迎 café https://entree.example/10086", 300)]
    [InlineData("code128", "ORDER10086", 203)]
    [InlineData("code128", "ORDER10086", 300)]
    [InlineData("code128", "ORDER10086", 600)]
    [InlineData("code39", "AB-123", 203)]
    [InlineData("code39", "AB-123", 300)]
    [InlineData("code39", "AB-123", 600)]
    public void ActualReceiptDrawing_DecodesToOriginalPayload(string format, string value, int dpi)
    {
        var code = VectorCodeRenderer.Encode(format, value, dpi: dpi);
        using var bitmap = new Bitmap((int)Math.Ceiling(code.Width * dpi / 96), (int)Math.Ceiling(code.Height * dpi / 96));
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.ScaleTransform(dpi / 96f, dpi / 96f);
            WindowsTextPrinter.Draw(graphics, new ReceiptTextLayout(code.Width, code.Height, [], code.Rectangles));
        }
        var bytes = new byte[bitmap.Width * bitmap.Height * 3];
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            var i = (y * bitmap.Width + x) * 3;
            bytes[i] = color.R; bytes[i + 1] = color.G; bytes[i + 2] = color.B;
        }
        var decoded = new BarcodeReaderGeneric { Options = new DecodingOptions { TryHarder = true } }
            .Decode(new RGBLuminanceSource(bytes, bitmap.Width, bitmap.Height, RGBLuminanceSource.BitmapFormat.RGB24));
        Assert.NotNull(decoded);
        Assert.Equal(value, decoded.Text);
        Assert.True(code.Rectangles.Min(rectangle => rectangle.X) > 0); // Quiet zone is retained.
        Assert.DoesNotContain("<img", VectorCodeRenderer.ToHtml(code, value));
        if (format != "qrcode") Assert.True(code.Rectangles.Min(rectangle => rectangle.Width) * 25.4f / 96 >= 0.25f);
    }

    [Theory]
    [InlineData("code39", "lowercase")]
    [InlineData("code128", "中文")]
    [InlineData("qrcode", "")]
    public void InvalidValuesAreRejectedWithoutRewriting(string format, string value)
    {
        Assert.Equal("BARCODE_VALUE_INVALID", Assert.Throws<CommandException>(() => VectorCodeRenderer.Encode(format, value)).Code);
    }

    [Fact]
    public void DenseBarcodeIsRejectedInsteadOfScaledUntilBlurry()
    {
        Assert.Equal("CODE_TOO_DENSE", Assert.Throws<CommandException>(() => VectorCodeRenderer.Encode("code128", new string('X', 70))).Code);
    }
}
