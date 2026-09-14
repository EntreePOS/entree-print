using System.Drawing;

namespace EntreePrintPlugin.Services;

// Split the saved display list at safe vertical boundaries, without reflow or scaling.
public static class ReceiptPaginator
{
    private const float Epsilon = 0.001f;

    public static IReadOnlyList<ReceiptTextLayout> Paginate(ReceiptTextLayout layout, float printableWidth, float printableHeight, int? dpi = null)
    {
        layout = VectorCodeRenderer.AlignToDots(layout);
        var codeResolutions = layout.Rectangles.Where(box => box.DotDpi.HasValue).Select(box => box.DotDpi!.Value).Distinct().ToArray();
        if (dpi is null && codeResolutions.Length > 0) dpi = codeResolutions[0];
        if (dpi is <= 0 || codeResolutions.Any(value => value != dpi))
            throw new CommandException("PRINTER_SETTINGS_CHANGED", "Prepared code resolution does not match the Windows page resolution.");
        float FloorToDot(float value) => dpi is { } resolution
            ? (float)(Math.Floor((value + Epsilon) * resolution / 96d) * 96d / resolution) : value;
        if (!float.IsFinite(printableWidth) || !float.IsFinite(printableHeight) || printableWidth < 1 || printableHeight < 1 ||
            layout.Width > printableWidth + 0.5f)
            throw new CommandException("LAYOUT_OVERFLOW", "The receipt cannot fit the Windows driver's printable width.");

        // DOM line bounds protect browser layout; these bounds also protect the actual GDI font extents.
        var textBounds = layout.Text.Select(run =>
        {
            var style = (run.Bold ? FontStyle.Bold : FontStyle.Regular) | (run.Italic ? FontStyle.Italic : FontStyle.Regular);
            using var font = new Font(run.Family, run.Size, style, GraphicsUnit.Pixel);
            var em = font.FontFamily.GetEmHeight(style);
            var top = run.Baseline - run.Size * font.FontFamily.GetCellAscent(style) / em;
            var bottom = run.Baseline + run.Size * font.FontFamily.GetCellDescent(style) / em;
            return (Top: Math.Max(0, top), Bottom: Math.Min(layout.Height, bottom));
        }).ToArray();
        var protectedBands = textBounds.Concat(layout.Rectangles.Where(box => box.DotDpi.HasValue)
                .Select(box => (Top: Math.Max(0, box.Y), Bottom: Math.Min(layout.Height, box.Y + box.Height))))
            .Concat((layout.KeepTogether ?? []).Select(region =>
            (Top: Math.Max(0, region.Y), Bottom: Math.Min(layout.Height, region.Y + region.Height))))
            .OrderBy(band => band.Top).ThenBy(band => band.Bottom).ToArray();
        var bands = new List<(float Top, float Bottom)>();
        foreach (var band in protectedBands)
        {
            // Overlapping regions must travel together, including columns on the same text line.
            if (bands.Count > 0 && band.Top < bands[^1].Bottom - Epsilon)
                bands[^1] = (bands[^1].Top, Math.Max(bands[^1].Bottom, band.Bottom));
            else bands.Add(band);
        }
        if (bands.Any(band => band.Bottom - band.Top > printableHeight + Epsilon))
            throw new CommandException("LAYOUT_ITEM_TOO_TALL", "A text line, code or keep-together section is taller than the Windows page. Use a longer page in Windows or a smaller section.");

        var pages = new List<ReceiptTextLayout>();
        for (float start = 0; start < layout.Height - Epsilon;)
        {
            var end = Math.Min(start + printableHeight, layout.Height);
            if (end < layout.Height - Epsilon) end = FloorToDot(end);
            while (true)
            {
                var crossing = bands.FindIndex(band => band.Top < end - Epsilon && band.Bottom > end + Epsilon);
                if (crossing < 0) break;
                end = FloorToDot(bands[crossing].Top);
            }
            if (end <= start + Epsilon || pages.Count >= 256)
                throw new CommandException("LAYOUT_OVERFLOW", "The receipt exceeds the supported Windows page count or cannot be divided safely.");

            var text = layout.Text.Where((run, index) => textBounds[index].Top >= start - Epsilon &&
                    textBounds[index].Bottom <= end + Epsilon && textBounds[index].Bottom > start + Epsilon)
                .Select(run => run with { Baseline = run.Baseline - start }).ToArray();
            // Backgrounds and borders may span pages. Clip them to each page instead of repeating them.
            var rectangles = layout.Rectangles.Where(box => box.Y < end && box.Y + box.Height > start)
                .Select(box => box with { Y = Math.Max(box.Y, start) - start,
                    Height = Math.Min(box.Y + box.Height, end) - Math.Max(box.Y, start) }).ToArray();
            var regions = (layout.KeepTogether ?? []).Where(region => region.Y >= start - Epsilon &&
                    region.Y + region.Height <= end + Epsilon && region.Y + region.Height > start + Epsilon)
                .Select(region => region with { Y = Math.Max(0, region.Y - start) }).ToArray();
            // Preserve content coordinates; a page is a slice of the original display list.
            var page = new ReceiptTextLayout(layout.Width, Math.Max(1, end - start), text, rectangles, regions);
            WindowsTextPrinter.Validate(page);
            pages.Add(page);
            start = end;
        }
        // The extractor adds a small bottom allowance. Do not turn it into a whole blank sheet.
        while (pages.Count > 1 && pages[^1].Text.Length == 0 && pages[^1].Rectangles.All(box =>
            box.Width == 0 || box.Height == 0 || ColorTranslator.FromHtml(box.Color).ToArgb() == Color.White.ToArgb()))
            pages.RemoveAt(pages.Count - 1);
        return pages;
    }
}
