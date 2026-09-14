using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EntreePrintPlugin.Services;

internal static class ChromiumTextLayout
{
    public static async Task<ReceiptTextLayout> PrepareAsync(string html, int widthMm, string directory, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        var browser = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft/Edge/Application/msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft/Edge/Application/msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google/Chrome/Application/chrome.exe")
        }.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("Install Microsoft Edge or Chrome to prepare positioned text.");
        var profile = Path.Combine(directory, "text-browser");
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
            "--disable-background-networking", "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1",
            "--user-data-dir=" + profile, "about:blank" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the layout browser.");
        try
        {
            var portFile = Path.Combine(profile, "DevToolsActivePort");
            string[] endpoint;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("The layout browser exited before it was ready.");
                endpoint = File.Exists(portFile) ? await File.ReadAllLinesAsync(portFile, token) : [];
                if (endpoint.Length >= 2) break;
                await Task.Delay(100, token);
            }
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{endpoint[0]}{endpoint[1]}"), token);
            var nextId = 0;
            async Task<JsonElement> Send(string method, object parameters, string? sessionId = null)
            {
                var id = ++nextId;
                var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters, sessionId },
                    new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, token);
                var buffer = new byte[65536];
                while (true)
                {
                    using var message = new MemoryStream();
                    ValueWebSocketReceiveResult read;
                    do
                    {
                        read = await socket.ReceiveAsync(buffer.AsMemory(), token);
                        if (read.MessageType == WebSocketMessageType.Close) throw new IOException("Layout browser disconnected.");
                        message.Write(buffer, 0, read.Count);
                        if (message.Length > 8_000_000) throw new InvalidOperationException("Layout exceeds the prototype size limit.");
                    } while (!read.EndOfMessage);
                    using var json = JsonDocument.Parse(message.ToArray());
                    if (!json.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
                    if (json.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.ToString());
                    return json.RootElement.GetProperty("result").Clone();
                }
            }
            var target = await Send("Target.createTarget", new { url = "about:blank" });
            var attached = await Send("Target.attachToTarget", new { targetId = target.GetProperty("targetId").GetString(), flatten = true });
            var session = attached.GetProperty("sessionId").GetString();
            await Send("Page.enable", new { }, session);
            await Send("Emulation.setDeviceMetricsOverride", new { width = (int)Math.Ceiling(widthMm / 25.4 * 96), height = 12000, deviceScaleFactor = 1, mobile = false }, session);
            await Send("Emulation.setEmulatedMedia", new { media = "screen" }, session);
            var frame = await Send("Page.getFrameTree", new { }, session);
            // The prototype accepts self-contained receipts only. Scripts and external resources cannot run/load.
            const string policy = "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'none'; font-src 'none'\">";
            var head = System.Text.RegularExpressions.Regex.Match(html, "<head\\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var safeHtml = head.Success ? html.Insert(head.Index + head.Length, policy) : "<!doctype html><head>" + policy + "</head><body>" + html + "</body>";
            await Send("Page.setDocumentContent", new { frameId = frame.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString(), html = safeHtml }, session);
            var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "extract-text-layout.js"), token);
            var result = await Send("Runtime.evaluate", new { expression = "(" + script + ")(" + JsonSerializer.Serialize(widthMm / 25.4 * 96) + ")", awaitPromise = true, returnByValue = true }, session);
            if (result.TryGetProperty("exceptionDetails", out var exception))
                throw new InvalidOperationException(exception.TryGetProperty("exception", out var detail) && detail.TryGetProperty("description", out var description) ? description.GetString() : exception.ToString());
            var layout = result.GetProperty("result").GetProperty("value").Deserialize<ReceiptTextLayout>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Browser did not return a text layout.");
            WindowsTextPrinter.Validate(layout);
            return WindowsTextPrinter.ResolveFonts(layout);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}
