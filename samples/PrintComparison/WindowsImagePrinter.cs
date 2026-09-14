using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;

namespace EntreePrintPlugin.Services;

// Historical A comparison only; not part of the production service.
public sealed class WindowsImagePrinter(decimal? paperWidthMm)
{
    private const decimal MillimetersPerInch = 25.4m;

    public void Print(string printerName, string imagePath, string documentName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows image printing requires Windows.");
        }

        if (string.IsNullOrWhiteSpace(printerName))
        {
            throw new ArgumentException("Printer name is required.", nameof(printerName));
        }

        using var image = Image.FromFile(imagePath);
        using var document = new PrintDocument
        {
            DocumentName = documentName
        };

        document.PrinterSettings.PrinterName = printerName;
        if (!document.PrinterSettings.IsValid)
        {
            throw new CommandException("PRINTER_INVALID", $"Unknown or unavailable printer: {printerName}");
        }

        document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

        if (paperWidthMm is decimal receiptPaperWidthMm && receiptPaperWidthMm > 0m)
        {
            var widthHundredths = MillimetersToHundredthsOfInch(receiptPaperWidthMm);
            var heightHundredths = ProportionalHeight(widthHundredths, image);
            document.DefaultPageSettings.PaperSize = new PaperSize("ENTREE Receipt", widthHundredths, heightHundredths);
        }

        document.PrintPage += (_, args) =>
        {
            if (args.Graphics is null)
            {
                throw new CommandException("PRINT_FAILED", "Printer graphics context is unavailable.");
            }

            var pageWidth = Math.Max(1, args.PageBounds.Width);
            var imageHeight = ProportionalHeight(pageWidth, image);
            args.Graphics.PageUnit = GraphicsUnit.Display;
            args.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            args.Graphics.CompositingQuality = CompositingQuality.HighSpeed;
            args.Graphics.SmoothingMode = SmoothingMode.None;
            args.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            args.Graphics.DrawImage(image, new Rectangle(0, 0, pageWidth, imageHeight));
            args.HasMorePages = false;
        };

        document.Print();
    }

    public static int MillimetersToHundredthsOfInch(decimal millimeters)
    {
        return Math.Max(1, (int)Math.Round(millimeters / MillimetersPerInch * 100m, MidpointRounding.AwayFromZero));
    }

    internal static int ProportionalHeight(int widthHundredths, Image image)
    {
        return Math.Max(1, (int)Math.Ceiling(widthHundredths * (image.Height / (double)image.Width)));
    }
}
