using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Drawing.Text;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace EntreePrintPlugin.Services;

// Experimental positioned-text path. Coordinates are CSS pixels at 96 px/inch.
// The demo prepares this immutable display list once for both preview and printing.
public sealed record ReceiptTextLayout(float Width, float Height, ReceiptTextRun[] Text, ReceiptRectangle[] Rectangles,
    ReceiptKeepTogether[]? KeepTogether = null);
public sealed record ReceiptKeepTogether(float Y, float Height);
public sealed record ReceiptTextRun(string Text, float X, float Baseline, float Width, float Size,
    string Family, bool Bold, bool Italic, string Color);
public sealed record ReceiptRectangle(float X, float Y, float Width, float Height, string Color, int? DotDpi = null);

public sealed class WindowsTextPrinter
{
    // Resolve CSS fallback stacks before freezing the display list. Otherwise GDI and
    // Chromium can choose different Chinese fonts for an Arial run.
    public static ReceiptTextLayout ResolveFonts(ReceiptTextLayout layout)
    {
        using var graphics = Graphics.FromHwnd(IntPtr.Zero);
        var hdc = graphics.GetHdc();
        try
        {
            return layout with { Text = layout.Text.Select(run =>
            {
                var candidates = run.Family.Split(',').Select(name => name.Trim().Trim('\'', '"'))
                    .Select(name => name switch { "sans-serif" => "Arial", "serif" => "Times New Roman", "monospace" => "Courier New", _ => name })
                    .Concat(["Microsoft YaHei", "Segoe UI"]).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in candidates)
                {
                    using var font = new Font(candidate, 12);
                    if (!font.FontFamily.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)) continue;
                    var handle = font.ToHfont();
                    var old = SelectObject(hdc, handle);
                    try
                    {
                        var glyphs = new ushort[run.Text.Length];
                        var result = GetGlyphIndices(hdc, run.Text, run.Text.Length, glyphs, 1);
                        if (result != uint.MaxValue && glyphs.All(glyph => glyph != ushort.MaxValue))
                            return run with { Family = font.FontFamily.Name };
                    }
                    finally { SelectObject(hdc, old); DeleteObject(handle); }
                }
                throw new CommandException("FONT_UNAVAILABLE", "No installed font in the receipt's font stack covers this text. Install an appropriate local font.");
            }).ToArray() };
        }
        finally { graphics.ReleaseHdc(hdc); }
    }

    [DllImport("gdi32.dll", EntryPoint = "GetGlyphIndicesW", CharSet = CharSet.Unicode)]
    private static extern uint GetGlyphIndices(IntPtr hdc, string text, int count, [Out] ushort[] glyphs, uint flags);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr item);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr item);

    public static void Validate(ReceiptTextLayout layout)
    {
        static bool Finite(float value) => float.IsFinite(value);
        if (!Finite(layout.Width) || !Finite(layout.Height) || layout.Width < 1 || layout.Width > 1000 ||
            layout.Height < 1 || layout.Height > 12000 || layout.Text.Length > 10000 || layout.Rectangles.Length > 10000 ||
            (layout.KeepTogether?.Length ?? 0) > 10000)
            throw new ArgumentException("Receipt dimensions or element count exceed the prototype limits.");
        foreach (var run in layout.Text)
            if (!Finite(run.X) || !Finite(run.Baseline) || !Finite(run.Width) || !Finite(run.Size) ||
                run.X < -0.5f || run.X + run.Width > layout.Width + 0.5f || run.Width <= 0 || run.Size <= 0 ||
                run.Baseline < 0 || run.Baseline > layout.Height + 0.5f)
                throw new ArgumentException("Receipt text overflows the selected print width or has invalid coordinates.");
        foreach (var box in layout.Rectangles)
            if (!Finite(box.X) || !Finite(box.Y) || !Finite(box.Width) || !Finite(box.Height) ||
                box.X < -0.5f || box.Y < -0.5f || box.Width < 0 || box.Height < 0 ||
                box.X + box.Width > layout.Width + 0.5f || box.Y + box.Height > layout.Height + 0.5f)
                throw new ArgumentException("Receipt graphics overflow the selected print area.");
        if (layout.Rectangles.Any(box => box.DotDpi is < 150 or > 1200))
            throw new ArgumentException("Code dot resolution exceeds supported limits.");
        foreach (var region in layout.KeepTogether ?? [])
            if (!Finite(region.Y) || !Finite(region.Height) || region.Y < -0.5f || region.Height <= 0 ||
                region.Y + region.Height > layout.Height + 0.5f)
                throw new ArgumentException("Receipt keep-together regions exceed the print area.");
    }

    public void Print(string printerName, ReceiptTextLayout layout, string documentName)
    {
        Validate(layout);
        using var document = new PrintDocument { DocumentName = documentName, PrintController = new StandardPrintController() };
        document.PrinterSettings.PrinterName = printerName;
        if (!document.PrinterSettings.IsValid) throw new ArgumentException("Select an installed Windows printer.");
        document.PrinterSettings.Copies = 1;
        document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        document.DefaultPageSettings.PaperSize = new PaperSize("ENTREE positioned text",
            (int)Math.Ceiling(layout.Width * 100 / 96), (int)Math.Ceiling(layout.Height * 100 / 96) + 8);
        document.PrintPage += (_, args) =>
        {
            var graphics = args.Graphics ?? throw new InvalidOperationException("Printer graphics are unavailable.");
            // Never shrink a whole receipt to fit: this would disguise a bad width profile.
            var printable = args.PageSettings.PrintableArea;
            if (layout.Width * 100 / 96 > Math.Min(printable.Width, args.PageBounds.Width) + 2 ||
                layout.Height * 100 / 96 > Math.Min(printable.Height, args.PageBounds.Height) + 2)
                throw new InvalidOperationException("The driver cannot fit this receipt. Check print width and custom paper length.");
            graphics.PageUnit = GraphicsUnit.Inch;
            graphics.PageScale = 1;
            graphics.ScaleTransform(1f / 96, 1f / 96);
            Draw(graphics, layout);
            args.HasMorePages = false;
        };
        document.Print();
    }

    // Shared by printing and the EMF proof artifact; no receipt-sized bitmap is created.
    public static void Draw(Graphics graphics, ReceiptTextLayout layout)
    {
        Validate(layout);
        graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        graphics.SmoothingMode = SmoothingMode.None;
        foreach (var box in layout.Rectangles)
        {
            using var brush = new SolidBrush(ColorTranslator.FromHtml(box.Color));
            graphics.FillRectangle(brush, box.X, box.Y, box.Width, box.Height);
        }
        using var format = new StringFormat(StringFormat.GenericTypographic)
        { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoClip };
        foreach (var run in layout.Text)
        {
            var style = (run.Bold ? FontStyle.Bold : FontStyle.Regular) | (run.Italic ? FontStyle.Italic : FontStyle.Regular);
            using var font = new Font(run.Family, run.Size, style, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(ColorTranslator.FromHtml(run.Color));
            var measured = graphics.MeasureString(run.Text, font, int.MaxValue, format).Width;
            var ascent = font.Size * font.FontFamily.GetCellAscent(font.Style) / font.FontFamily.GetEmHeight(font.Style);
            var saved = graphics.Save();
            try
            {
                graphics.TranslateTransform(run.X, run.Baseline - ascent);
                // Match Chromium's line extent despite differences in GDI font metrics.
                graphics.ScaleTransform(run.Width / Math.Max(0.01f, measured), 1);
                graphics.DrawString(run.Text, font, brush, 0, 0, format);
            }
            finally { graphics.Restore(saved); }
        }
    }

    public static void WriteMetafile(string path, ReceiptTextLayout layout)
    {
        Validate(layout);
        using var reference = Graphics.FromHwnd(IntPtr.Zero);
        var hdc = reference.GetHdc();
        Metafile metafile;
        try { metafile = new Metafile(path, hdc, new RectangleF(0, 0, layout.Width, layout.Height), MetafileFrameUnit.Pixel, EmfType.EmfPlusDual); }
        finally { reference.ReleaseHdc(hdc); }
        using (metafile)
        using (var graphics = Graphics.FromImage(metafile))
        {
            graphics.PageUnit = GraphicsUnit.Pixel;
            Draw(graphics, layout);
        }
    }

    public static string ToSvg(ReceiptTextLayout layout)
    {
        Validate(layout);
        static string N(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        static string E(string value) => WebUtility.HtmlEncode(value);
        var svg = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(layout.Width / 96 * 25.4f)}mm\" height=\"{N(layout.Height / 96 * 25.4f)}mm\" viewBox=\"0 0 {N(layout.Width)} {N(layout.Height)}\"><rect width=\"100%\" height=\"100%\" fill=\"white\"/>");
        foreach (var box in layout.Rectangles)
            svg.Append($"<rect x=\"{N(box.X)}\" y=\"{N(box.Y)}\" width=\"{N(box.Width)}\" height=\"{N(box.Height)}\" fill=\"{E(box.Color)}\"/>");
        foreach (var run in layout.Text)
            svg.Append($"<text xml:space=\"preserve\" x=\"{N(run.X)}\" y=\"{N(run.Baseline)}\" font-family=\"{E(run.Family)}\" font-size=\"{N(run.Size)}\" font-weight=\"{(run.Bold ? "bold" : "normal")}\" font-style=\"{(run.Italic ? "italic" : "normal")}\" fill=\"{E(run.Color)}\" textLength=\"{N(run.Width)}\" lengthAdjust=\"spacingAndGlyphs\">{E(run.Text)}</text>");
        return svg.Append("</svg>").ToString();
    }
}
