using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using EntreePrintPlugin.Services;

// Historical A image rendering belongs to the comparison demo, not the print service.
internal static class ComparisonScreenshotRenderer
{
    public static async Task<string> RenderAsync(string html, int widthMm, string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var htmlPath = Path.Combine(directory, "receipt.html");
        var imagePath = Path.Combine(directory, "receipt.png");
        await File.WriteAllTextAsync(htmlPath, HtmlPatcher.Patch(html, ""), token);
        var browser = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft/Edge/Application/msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft/Edge/Application/msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google/Chrome/Application/chrome.exe")
        }.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("Install Edge or Chrome to run the image comparison.");
        var start = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { "--headless=new", "--disable-gpu", "--disable-extensions", "--disable-background-networking",
            "--no-first-run", "--no-default-browser-check", "--force-device-scale-factor=2",
            $"--window-size={(int)Math.Ceiling(widthMm / 25.4 * 96)},4000", "--user-data-dir=" + Path.Combine(directory, "browser"),
            "--screenshot=" + imagePath, new Uri(htmlPath).AbsoluteUri }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the comparison browser.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0 || !File.Exists(imagePath) || new FileInfo(imagePath).Length == 0)
                throw new InvalidOperationException("The browser did not produce the comparison image.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
        var trimmedPath = Path.Combine(directory, "trimmed.png");
        using (var bitmap = new Bitmap(imagePath))
        {
            var bottom = -1;
            for (var y = bitmap.Height - 1; y >= 0 && bottom < 0; y--)
                for (var x = 0; x < bitmap.Width; x += 4)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.A > 16 && (pixel.R < 245 || pixel.G < 245 || pixel.B < 245)) { bottom = y; break; }
                }
            var height = bottom < 0 ? bitmap.Height : Math.Min(bitmap.Height, bottom + 25);
            using var cropped = bitmap.Clone(new Rectangle(0, 0, bitmap.Width, height), bitmap.PixelFormat);
            cropped.SetResolution(bitmap.HorizontalResolution, bitmap.VerticalResolution);
            cropped.Save(trimmedPath, ImageFormat.Png);
        }
        return trimmedPath;
    }
}
