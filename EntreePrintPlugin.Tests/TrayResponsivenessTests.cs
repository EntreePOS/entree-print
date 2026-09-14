using EntreePrintTray;

namespace EntreePrintPlugin.Tests;

public sealed class TrayResponsivenessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceWork_KeepsMessageLoopResponsiveAndPreventsOverlap(bool disposeWhileWorking)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "EntreeTrayTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "settings.json");
            using var release = new ManualResetEventSlim();
            try
            {
                new PluginConfig { AutoInstallWindowsService = false }.Save(path);
                using var guard = SingleInstanceGuard.TryAcquire(@"Local\EntreeTrayTests." + Guid.NewGuid().ToString("N"));
                using var context = new TrayApplicationContext(guard, showSettings: false, path);
                using var starter = new System.Windows.Forms.Timer { Interval = 10 };
                using var pulse = new System.Windows.Forms.Timer { Interval = 10 };
                var ticks = 0;
                pulse.Tick += (_, _) => { if (++ticks >= 3) release.Set(); };
                starter.Tick += async (_, _) =>
                {
                    starter.Stop();
                    try
                    {
                        var uiThread = Environment.CurrentManagedThreadId;
                        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var operation = context.RunServiceActionAsync("test service operation", () =>
                        {
                            Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
                            started.SetResult();
                            Assert.True(release.Wait(TimeSpan.FromSeconds(5)), "The UI message loop did not release the worker.");
                            if (disposeWhileWorking) throw new InvalidOperationException("Late worker failure after tray closed");
                        });
                        await started.Task;
                        Assert.Equal(uiThread, Environment.CurrentManagedThreadId);
                        Assert.False(operation.IsCompleted);
                        var overlappingCalls = 0;
                        await context.RunServiceActionAsync("overlapping operation", () => overlappingCalls++);
                        Assert.Equal(0, overlappingCalls);
                        if (disposeWhileWorking) context.Dispose();
                        pulse.Start();
                        await operation;
                        Assert.True(ticks >= 3);
                        if (!disposeWhileWorking)
                        {
                            await context.RunServiceActionAsync("next operation", () => overlappingCalls++);
                            Assert.Equal(1, overlappingCalls);
                        }
                        completed.SetResult();
                    }
                    catch (Exception error) { completed.TrySetException(error); }
                    finally { release.Set(); System.Windows.Forms.Application.ExitThread(); }
                };
                starter.Start();
                System.Windows.Forms.Application.Run();
            }
            catch (Exception error) { completed.TrySetException(error); }
            finally { release.Set(); Directory.Delete(directory, recursive: true); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task MalformedSettings_DoNotPreventTrayCreationOrOverwriteTheFile()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), "entree-bad-settings-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{broken settings");
                using var guard = SingleInstanceGuard.TryAcquire(@"Local\EntreeTrayTests." + Guid.NewGuid().ToString("N"));
                using var context = new TrayApplicationContext(guard, showSettings: false, path);
                Assert.Equal("{broken settings", File.ReadAllText(path));
                completed.SetResult();
            }
            catch (Exception error) { completed.TrySetException(error); }
            finally { File.Delete(path); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
