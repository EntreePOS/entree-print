using System.Globalization;
using System.Net;
using System.Text;
using ZXing;
using ZXing.Common;

namespace EntreePrintPlugin.Services;

public sealed record VectorCode(float Width, float Height, ReceiptRectangle[] Rectangles);

public static class VectorCodeRenderer
{
    public static VectorCode Encode(string format, string value, decimal availableWidthMm = 72, int dpi = 203,
        decimal sizeMm = 24, decimal heightMm = 12)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 1024)
            throw new CommandException("BARCODE_VALUE_INVALID", "Code values must contain 1–1024 characters.");
        var type = format switch
        {
            "qrcode" => BarcodeFormat.QR_CODE,
            "code128" => BarcodeFormat.CODE_128,
            "code39" => BarcodeFormat.CODE_39,
            _ => throw new CommandException("BARCODE_FORMAT_UNSUPPORTED", "Use qrcode, code128 or code39.")
        };
        if (type == BarcodeFormat.CODE_128 && value.Any(c => c < 32 || c > 126)
            || type == BarcodeFormat.CODE_39 && value.Any(c => !"0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%".Contains(c)))
            throw new CommandException("BARCODE_VALUE_INVALID", "The value contains characters unsupported by this barcode format.");
        if (dpi < 150 || dpi > 1200 || availableWidthMm < 20 || availableWidthMm > 100
            || sizeMm < 10 || sizeMm > 100 || heightMm < 5 || heightMm > 100)
            throw new CommandException("LAYOUT_INVALID", "Code dimensions or printer resolution are outside supported limits.");
        var qr = type == BarcodeFormat.QR_CODE;
        BitMatrix matrix;
        try
        {
            var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = qr ? 4 : 20 };
            if (qr) { hints[EncodeHintType.CHARACTER_SET] = "UTF-8"; hints[EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M; }
            matrix = new MultiFormatWriter().encode(value, type, 0, qr ? 0 : 1, hints);
        }
        catch (ArgumentException error) { throw new CommandException("BARCODE_VALUE_INVALID", error.Message); }
        var targetDots = (int)Math.Floor(Math.Min(qr ? sizeMm : availableWidthMm, availableWidthMm) * dpi / 25.4m);
        // Preserve physical bar width across driver resolutions (roughly 0.25–0.38 mm),
        // instead of turning the 203-DPI three-dot bars into hairlines on a 600-DPI driver.
        var preferredBarDots = Math.Max(2, (int)Math.Round(dpi * 0.375 / 25.4));
        var minimumBarDots = Math.Max(2, (int)Math.Ceiling(dpi * 0.25 / 25.4));
        var moduleDots = qr ? targetDots / matrix.Width : Math.Min(preferredBarDots, targetDots / matrix.Width);
        if (moduleDots < (qr ? 3 : minimumBarDots))
            throw new CommandException("CODE_TOO_DENSE", "Code cannot fit with readable modules. Shorten its value or increase its available size.");
        var unit = moduleDots * 96f / dpi;
        var height = qr ? matrix.Height * unit : (float)Math.Round(heightMm * dpi / 25.4m) * 96f / dpi;
        var rectangles = new List<ReceiptRectangle>();
        for (var y = 0; y < matrix.Height; y++)
        {
            for (var x = 0; x < matrix.Width;)
            {
                if (!matrix[x, y]) { x++; continue; }
                var start = x;
                while (x < matrix.Width && matrix[x, y]) x++;
                rectangles.Add(new(start * unit, qr ? y * unit : 0, (x - start) * unit, qr ? unit : height, "#000000", dpi));
            }
        }
        return new(matrix.Width * unit, height, rectangles.ToArray());
    }

    public static string ToHtml(VectorCode code, string value, string align = "center", bool showText = false)
    {
        if (align is not ("left" or "center" or "right")) throw new CommandException("CONTENT_INVALID", "Code alignment must be left, center or right.");
        static string N(float n) => n.ToString("0.#####", CultureInfo.InvariantCulture);
        var margin = align == "left" ? "0 auto 0 0" : align == "right" ? "0 0 0 auto" : "0 auto";
        var html = new StringBuilder($"<div style=\"break-inside:avoid\"><div style=\"display:block;padding:0;margin:{margin};position:relative;background:#fff;width:{N(code.Width)}px;height:{N(code.Height)}px\">");
        foreach (var r in code.Rectangles)
        {
            if (r.DotDpi is not { } dpi) throw new ArgumentException("Code geometry must include its printer resolution.");
            var dots = string.Join(" ", new[] { r.X, r.Y, r.Width, r.Height }.Select(value => MathF.Round(value * dpi / 96).ToString(CultureInfo.InvariantCulture)));
            html.Append($"<b data-entree-dot-dpi=\"{dpi}\" data-entree-dot-box=\"{dots}\" style=\"position:absolute;display:block;padding:0;margin:0;border:0;left:{N(r.X)}px;top:{N(r.Y)}px;width:{N(r.Width)}px;height:{N(r.Height)}px;background:#000\"></b>");
        }
        html.Append("</div>");
        if (showText) html.Append($"<div style=\"font:14px Arial,sans-serif;text-align:{align};margin:4px 0 0\">{WebUtility.HtmlEncode(value)}</div>");
        return html.Append("</div>").ToString();
    }

    public static ReceiptTextLayout AlignToDots(ReceiptTextLayout layout)
    {
        WindowsTextPrinter.Validate(layout);
        var aligned = layout with { Rectangles = layout.Rectangles.Select(box =>
        {
            if (box.DotDpi is not { } dpi) return box;
            static float Snap(float value, int dpi) => MathF.Round(value * dpi / 96f) * 96f / dpi;
            var x = Snap(box.X, dpi);
            var y = Snap(box.Y, dpi);
            return box with { X = x, Y = y, Width = Snap(box.X + box.Width, dpi) - x,
                Height = Snap(box.Y + box.Height, dpi) - y };
        }).ToArray() };
        WindowsTextPrinter.Validate(aligned);
        return aligned;
    }
}
