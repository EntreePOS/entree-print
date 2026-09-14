using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntreePrintPlugin;
using EntreePrintPlugin.Models;
using EntreePrintPlugin.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntreePrintPlugin.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task CommandProcessor_AcksDuplicateIdWithoutExecutingTwice()
    {
        var backend = new RecordingBackend();
        var processor = CreateProcessor(backend);
        var body = BuildCommand(new
        {
            id = "retry-1",
            type = "print",
            printer = "Kitchen",
            html = "<h1>Retry</h1>",
            style = "",
            token = ""
        });

        var first = processor.AcceptValidated(body);
        var second = processor.AcceptValidated(body);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Job.Id, second.Job.Id);
        await WaitForAsync(() => backend.Executed.Count == 1);
        Assert.Single(backend.Executed);
    }

    [Fact]
    public async Task CommandProcessor_SerializesJobsForSamePrinter()
    {
        var backend = new BlockingBackend();
        var processor = CreateProcessor(backend);

        processor.AcceptValidated(BuildCommand(new
        {
            id = "same-1",
            type = "print",
            printer = "Kitchen",
            html = "<h1>First</h1>",
            style = "",
            token = ""
        }));
        processor.AcceptValidated(BuildCommand(new
        {
            id = "same-2",
            type = "print",
            printer = "Kitchen",
            html = "<h1>Second</h1>",
            style = "",
            token = ""
        }));

        await WaitForAsync(() => backend.StartedIds.Count == 1);
        Assert.Equal(["same-1"], backend.StartedIds);

        backend.ReleaseOne();
        await WaitForAsync(() => backend.StartedIds.Count == 2);
        Assert.Equal(["same-1", "same-2"], backend.StartedIds);
        backend.ReleaseOne();
    }

    [Fact]
    public async Task CommandProcessor_AllowsDifferentPrintersToRunIndependently()
    {
        var backend = new BlockingBackend();
        var processor = CreateProcessor(backend);

        processor.AcceptValidated(BuildCommand(new
        {
            id = "cross-1",
            type = "print",
            printer = "Kitchen",
            html = "<h1>Kitchen</h1>",
            style = "",
            token = ""
        }));
        processor.AcceptValidated(BuildCommand(new
        {
            id = "cross-2",
            type = "print",
            printer = "Bar",
            html = "<h1>Bar</h1>",
            style = "",
            token = ""
        }));

        await WaitForAsync(() => backend.StartedIds.Count == 2);
        Assert.Contains("cross-1", backend.StartedIds);
        Assert.Contains("cross-2", backend.StartedIds);
        backend.ReleaseOne();
        backend.ReleaseOne();
    }

    [Fact]
    public async Task CommandProcessor_RetriesOnlyProvenNotSubmittedFailure()
    {
        var backend = new FailsOnceBackend("paper out");
        var settings = new PluginSettings
        {
            PrintRetryMaxAttempts = 2,
            PrintRetryDelayMs = 1
        };
        var events = new EventBroadcaster();
        var jobs = new JobStore(events);
        var processor = CreateProcessor(backend, settings, jobs, events);
        var body = BuildCommand(new
        {
            id = "paper-retry-1",
            type = "print",
            printer = "Kitchen",
            html = "<h1>Retry paper</h1>",
            style = "",
            token = ""
        });

        processor.AcceptValidated(body);

        await WaitForAsync(() => backend.Attempts == 2);
        var job = jobs.Get("paper-retry-1");
        Assert.NotNull(job);
        Assert.Equal("completed", job.Status);
        Assert.Equal(2, job.Attempts);
    }

    private static AcceptedCommand BuildCommand(object source)
    {
        var value = JsonSerializer.SerializeToElement(source);
        return new AcceptedCommand(value.GetProperty("id").GetString()!, value.GetProperty("type").GetString()!,
            value.GetProperty("printer").GetString()!, value.GetProperty("html").GetString()!, "", "", null);
    }
    private static CommandProcessor CreateProcessor(
        IPrinterBackend backend,
        PluginSettings? settings = null,
        JobStore? jobs = null,
        EventBroadcaster? events = null)
    {
        settings ??= new PluginSettings();
        events ??= new EventBroadcaster();
        jobs ??= new JobStore(events);
        var status = new PrinterStatusService(
            settings,
            events,
            NullLogger<PrinterStatusService>.Instance);

        return new CommandProcessor(
            settings,

            jobs,
            backend,
            new PrinterExecutionQueue(NullLogger<PrinterExecutionQueue>.Instance),
            status,
            NullLogger<CommandProcessor>.Instance);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(50);
        }
        Assert.True(condition());
    }

    private static bool ContainsBytes(byte[] source, byte[] expected)
    {
        if (expected.Length == 0)
        {
            return true;
        }

        for (var index = 0; index <= source.Length - expected.Length; index++)
        {
            if (source.AsSpan(index, expected.Length).SequenceEqual(expected))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class RecordingBackend : IPrinterBackend
    {
        public List<AcceptedCommand> Executed { get; } = [];

        public Task<PrintExecutionResult> ExecuteAsync(AcceptedCommand command, CancellationToken cancellationToken)
        {
            Executed.Add(command);
            return Task.FromResult(new PrintExecutionResult("completed"));
        }
    }

    private sealed class BlockingBackend : IPrinterBackend
    {
        private readonly Queue<TaskCompletionSource> _releases = new();
        private readonly object _gate = new();

        public List<string> StartedIds { get; } = [];

        public Task<PrintExecutionResult> ExecuteAsync(AcceptedCommand command, CancellationToken cancellationToken)
        {
            TaskCompletionSource release;
            lock (_gate)
            {
                StartedIds.Add(command.Id);
                release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _releases.Enqueue(release);
            }

            return WaitThenCompleteAsync(release.Task);
        }

        public void ReleaseOne()
        {
            TaskCompletionSource? release = null;
            lock (_gate)
            {
                if (_releases.Count > 0)
                {
                    release = _releases.Dequeue();
                }
            }
            release?.SetResult();
        }

        private static async Task<PrintExecutionResult> WaitThenCompleteAsync(Task release)
        {
            await release;
            return new PrintExecutionResult("completed");
        }
    }

    private sealed class FailsOnceBackend(string message) : IPrinterBackend
    {
        public int Attempts { get; private set; }

        public Task<PrintExecutionResult> ExecuteAsync(AcceptedCommand command, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts == 1)
            {
                throw new PrintNotSubmittedException(message);
            }

            return Task.FromResult(new PrintExecutionResult("completed"));
        }
    }
}
