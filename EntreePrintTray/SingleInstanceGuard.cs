namespace EntreePrintTray;

public sealed class SingleInstanceGuard : IDisposable
{
    public const string DefaultMutexName = @"Local\EntreePrintPlugin.Tray";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle _activation;

    private SingleInstanceGuard(Mutex? mutex, EventWaitHandle activation, bool isPrimary)
    {
        _mutex = mutex;
        _activation = activation;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }

    public void RequestActivation() => _activation.Set();
    public bool ConsumeActivation() => _activation.WaitOne(0);

    public static SingleInstanceGuard TryAcquire(string mutexName = DefaultMutexName)
    {
        var mutex = new Mutex(false, mutexName);
        var activation = new EventWaitHandle(false, EventResetMode.AutoReset, mutexName + ".Activate");
        try
        {
            if (!mutex.WaitOne(0, false))
            {
                mutex.Dispose();
                return new SingleInstanceGuard(null, activation, false);
            }

            return new SingleInstanceGuard(mutex, activation, true);
        }
        catch (AbandonedMutexException)
        {
            // WaitOne grants ownership when the preceding process exited unexpectedly.
            return new SingleInstanceGuard(mutex, activation, true);
        }
        catch
        {
            mutex.Dispose();
            activation.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _activation.Dispose();
        if (_mutex is null)
        {
            return;
        }

        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
