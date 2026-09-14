using EntreePrintPlugin.Services;

namespace EntreePrintPlugin.Tests;

public sealed class BrowserEndpointTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "EntreeBrowserEndpointTests", Guid.NewGuid().ToString("N"));
    public BrowserEndpointTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public async Task WaitsForChromiumToReleaseItsEndpointFile()
    {
        var path = Path.Combine(directory, "DevToolsActivePort");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        Task<string[]> waiting;
        await using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await writer.WriteAsync("12345\n/devtools/browser/test\n"u8.ToArray());
            await writer.FlushAsync();
            waiting = ReceiptLayoutEngine.WaitForBrowserEndpointAsync(path, () =>
            {
                if (++reads >= 2) retried.TrySetResult();
                return false;
            }, timeout.Token);
            await retried.Task.WaitAsync(timeout.Token);
            Assert.False(waiting.IsCompleted);
        }
        Assert.Equal(new[] { "12345", "/devtools/browser/test" }, await waiting);
    }

    [Fact]
    public async Task WaitsForMissingAndIncompleteEndpointWithinTheSameDeadline()
    {
        var path = Path.Combine(directory, "DevToolsActivePort");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reads = 0;
        var waiting = ReceiptLayoutEngine.WaitForBrowserEndpointAsync(path, () =>
        {
            // Publish between polling reads so fixture writes cannot race the reader's handle.
            if (++reads == 2) File.WriteAllText(path, "12345\n");
            if (reads == 4) File.WriteAllText(path, "12345\n/devtools/browser/test\n");
            return false;
        }, timeout.Token);
        Assert.Equal(2, (await waiting).Length);
        Assert.Equal(4, reads);
    }

    [Fact]
    public async Task CancellationStopsWaitingForALockedFile()
    {
        var path = Path.Combine(directory, "DevToolsActivePort");
        using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = ReceiptLayoutEngine.WaitForBrowserEndpointAsync(path, () => false, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task BrowserExitAndPermissionErrorsAreNotRetried()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReceiptLayoutEngine.WaitForBrowserEndpointAsync(
            Path.Combine(directory, "missing"), () => true, CancellationToken.None));
        // A directory in place of the file produces access denied, not a transient writer lock.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ReceiptLayoutEngine.WaitForBrowserEndpointAsync(
            directory, () => false, CancellationToken.None));
    }
}
