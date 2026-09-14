# Process termination recovery

Runs isolated child processes using the production file-backed job ledger, then forcibly terminates each child after its durable checkpoint. It never opens a printer or stops the installed service. The parent kills only process handles that it created and verifies the child's ready marker against its PID.

```powershell
dotnet run --project samples/RecoverySmoke/RecoverySmoke.csproj --artifacts-path "$env:TEMP\EntreeRecoverySmokeBuild" -- "$env:TEMP\EntreeRecoverySmoke-new-run"
```

Use a fresh output directory. All five checkpoints passed on 2026-09-13 local time; see [recorded results](observed-results.json):

- Durable acceptance: unsent receipt recovered, eligible for one delivery worker.
- Attempt identity saved before Windows ID: unknown outcome, no automatic replay.
- Windows ID saved during handoff: needs attention, no automatic replay.
- Full handoff saved: submitted job preserved, no automatic replay.
- Completion saved: completed job preserved, no automatic replay.

At every checkpoint, replaying the original intent returns its existing record and changing the payload rejects with `IDEMPOTENCY_CONFLICT`. Windows IDs/status flags here are fixtures; real Windows submission/notifications are tested separately in BetaSmoke. This does not emulate power loss or a disk that ignores flushes.
