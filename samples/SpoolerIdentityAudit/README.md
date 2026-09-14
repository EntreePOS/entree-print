# Windows handoff identity audit

```powershell
dotnet run --project samples/SpoolerIdentityAudit
```

Requires the repository's .NET 10 SDK/runtime and the installed **Microsoft Print to PDF** virtual queue. This sample creates one partial Windows job, deliberately omits saving the Windows job ID, reopens its temporary durable ledger, and matches the job using `EnumJobsW` plus the unique document name saved before StartDoc. It checks that the job remains uncertain, cannot be automatically retried, and cannot be explicitly reprinted while Windows still reports it active.

The callback then stops before drawing any page or calling EndDoc. The renderer's cleanup calls AbortDoc, and the sample checks that its exact Windows ID/document pair disappears. No default/thermal queue is selected, no service/listener is started, and unrelated jobs are untouched. Temporary ledger and `result.json` remain under `%TEMP%/EntreeSpoolerIdentityAudit` for inspection.

This exercises native queue enumeration and ledger reopen in the StartDoc-to-ID-save gap. It does not kill the Windows service/process, prove complete receipt delivery, or test physical hardware. Missing, ambiguous, mismatched, failed-storage and aged-job cases are covered separately by `SpoolerIdentityRecoveryTests`.
