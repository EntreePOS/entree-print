using EntreePrintTray;

namespace EntreePrintPlugin.Tests;

public sealed class TrayMenuRecoveryTests
{
    [Fact]
    public async Task UnreadableSettingsInPortsOrDiagnosticsKeepTrayUsableAndPreserveTheFile()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "EntreeTrayMenuTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "settings.json");
            try
            {
                new PluginConfig { AutoInstallWindowsService = false }.Save(path);
                using var guard = SingleInstanceGuard.TryAcquire(@"Local\EntreeTrayMenuTests." + Guid.NewGuid().ToString("N"));
                using var context = new TrayApplicationContext(guard, showSettings: false, path);
                File.WriteAllText(path, "{broken settings");
                var windows = System.Windows.Forms.Application.OpenForms.Count;
                Assert.False(context.RunMenuAction(context.ShowPorts));
                Assert.False(context.RunMenuAction(context.ShowDiagnostics));
                Assert.Equal(windows, System.Windows.Forms.Application.OpenForms.Count);
                Assert.Equal("{broken settings", File.ReadAllText(path));
                var next = 0;
                Assert.True(context.RunMenuAction(() => next++));
                Assert.Equal(1, next);
                Assert.False(context.RunMenuAction(() => throw new UnauthorizedAccessException("Folder access denied")));
                context.Dispose();
                Assert.False(context.RunMenuAction(() => next++));
                Assert.Equal(1, next);
                done.SetResult();
            }
            catch (Exception error) { done.TrySetException(error); }
            finally { Directory.Delete(directory, true); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("PrinterStatusRefreshSeconds", 0)]
    [InlineData("PrinterStatusRefreshSeconds", 3601)]
    [InlineData("PrintRetryMaxAttempts", -1)]
    [InlineData("PrintRetryMaxAttempts", 10001)]
    [InlineData("PrintRetryDelayMs", -1)]
    [InlineData("PrintRetryDelayMs", 601000)]
    public void OutOfRangeSavedNumbersFailBeforeConstructingSettingsControls(string property, int value)
    {
        var path = Path.Combine(Path.GetTempPath(), "entree-invalid-setting-" + Guid.NewGuid().ToString("N") + ".json");
        var content = "{\"" + property + "\":" + value + "}";
        try
        {
            File.WriteAllText(path, content);
            Assert.Throws<ArgumentException>(() => PluginConfig.Load(path));
            Assert.Equal(content, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
