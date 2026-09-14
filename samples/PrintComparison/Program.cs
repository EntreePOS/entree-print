using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Printing;
using System.Text.Json;
using EntreePrintPlugin;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:19779");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 300_000);
var app = builder.Build();
var sessionToken = Guid.NewGuid().ToString("N");
var spool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "EntreePrintComparison", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId);
var prepared = new ConcurrentDictionary<string, PreparedReceipt>();
var submissions = new ConcurrentDictionary<string, (string Signature, Lazy<Task<object>> Work)>();
using var renderGate = new SemaphoreSlim(1);
using var printGate = new SemaphoreSlim(1);

app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    if (context.Request.Host.Host is not ("127.0.0.1" or "localhost"))
    {
        context.Response.StatusCode = 403;
        return;
    }
    if (context.Request.Method == "POST" &&
        context.Request.Headers["X-Demo-Session"].ToString() != sessionToken)
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { error = "Reload the comparison page before continuing." });
        return;
    }
    try { await next(); }
    catch (Exception error)
    {
        app.Logger.LogWarning(error, "Comparison request failed");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = error.Message });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/session", () => new
{
    token = sessionToken,
    printers = PrinterSettings.InstalledPrinters.Cast<string>().OrderBy(name => name).ToArray(),
    defaultPrinter = new PrinterSettings().PrinterName
});

app.MapPost("/api/prepare", async (PrepareRequest request) =>
{
    if (request.PaperWidthMm is not (48 or 58 or 72 or 80) || string.IsNullOrWhiteSpace(request.Html) || request.Html.Length > 250_000)
        return Results.BadRequest(new { error = "Choose a supported print width and provide receipt HTML under 250 KB." });
    if (prepared.Count >= 50)
        return Results.BadRequest(new { error = "This demo session has reached 50 renders. Restart it to begin a new session." });

    // The historical image comparison is isolated from the production print backend.
    var id = Guid.NewGuid().ToString("N");
    await renderGate.WaitAsync();
    try
    {
        var imagePath = await ComparisonScreenshotRenderer.RenderAsync(request.Html, request.PaperWidthMm, Path.Combine(spool, id), CancellationToken.None);
        using var bitmap = Image.FromFile(imagePath);
        var heightMm = (double)request.PaperWidthMm * bitmap.Height / bitmap.Width;
        ReceiptTextLayout? textLayout = null;
        string? textError = null;
        try
        {
            var directory = Path.Combine(spool, id);
            textLayout = await ChromiumTextLayout.PrepareAsync(request.Html, request.PaperWidthMm, directory, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(directory, "layout.json"), JsonSerializer.Serialize(textLayout, new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllTextAsync(Path.Combine(directory, "preview.svg"), WindowsTextPrinter.ToSvg(textLayout));
            WindowsTextPrinter.WriteMetafile(Path.Combine(directory, "receipt.emf"), textLayout);
        }
        catch (Exception error)
        {
            textError = error is OperationCanceledException ? "D layout timed out. Prepare again." : error.Message;
            app.Logger.LogWarning(error, "Positioned-text preparation failed");
        }
        prepared[id] = new PreparedReceipt(imagePath, request.PaperWidthMm, request.Html, textLayout);
        return Results.Json(new { id, imageUrl = $"/api/image/{id}", width = bitmap.Width, height = bitmap.Height,
            paperWidthMm = request.PaperWidthMm, heightMm, renderScale = 2,
            textPreviewUrl = textLayout is null ? null : $"/api/text-preview/{id}", textRuns = textLayout?.Text.Length, textError });
    }
    finally { renderGate.Release(); }
});

app.MapGet("/api/image/{id}", (string id) => prepared.TryGetValue(id, out var receipt)
    ? Results.File(receipt.ImagePath, "image/png") : Results.NotFound());

app.MapGet("/api/text-preview/{id}", (string id) => prepared.TryGetValue(id, out var receipt) && receipt.TextLayout is not null
    ? Results.Text(WindowsTextPrinter.ToSvg(receipt.TextLayout), "image/svg+xml") : Results.NotFound());

app.MapPost("/api/print", async (PrintRequest request) =>
{
    if (!Guid.TryParseExact(request.RequestId, "N", out _) || !prepared.TryGetValue(request.Id, out var receipt))
        return Results.BadRequest(new { error = "Prepare the receipt first and provide a valid submission ID." });
    if (!PrinterSettings.InstalledPrinters.Cast<string>().Contains(request.Printer, StringComparer.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Select an installed Windows printer." });
    var method = request.Method ?? "a";
    if (method is not ("a" or "d") || (method == "d" && receipt.TextLayout is null))
        return Results.BadRequest(new { error = "Prepare a supported receipt before printing D." });
    var signature = request.Id + "\n" + request.Printer + "\n" + method;
    var submission = submissions.GetOrAdd(request.RequestId, _ => (signature, new Lazy<Task<object>>(async () =>
    {
        await printGate.WaitAsync();
        try
        {
            // Prints the exact image already displayed; no hidden rerender or automatic retry.
            if (method == "d") new WindowsTextPrinter().Print(request.Printer, receipt.TextLayout!, $"ENTREE Compare D {request.Id[..8]}");
            else new WindowsImagePrinter(receipt.PaperWidthMm).Print(request.Printer, receipt.ImagePath, $"ENTREE Compare A {request.Id[..8]}");
            return new { status = "submitted", message = $"{method.ToUpperInvariant()} submitted to Windows. Check the paper output; this is not physical completion confirmation." };
        }
        finally { printGate.Release(); }
    })));
    if (submission.Signature != signature)
        return Results.Conflict(new { error = "This submission ID belongs to a different receipt or printer." });
    return Results.Json(await submission.Work.Value);
});

app.Run();

record PrepareRequest(string Html, int PaperWidthMm);
record PrintRequest(string Id, string Printer, string RequestId, string? Method = null);
record PreparedReceipt(string ImagePath, int PaperWidthMm, string Html, ReceiptTextLayout? TextLayout);
