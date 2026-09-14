# Wi-Fi failure characterization

The C# diagnostic now checks the repaired durable-acceptance/replay behavior. The Node diagnostic still characterizes the unchanged POS/C-Lodop wrapper. Neither submits physical print jobs or changes service/printer settings. The C# harness uses a temporary on-disk ledger and removes only its own temporary directory when finished.

From the print-plugin repository root:

```powershell
dotnet run --project samples/WifiFailureAudit/WifiFailureAudit.csproj --artifacts-path "$env:TEMP\EntreeWifiFailureAudit" -- samples/WifiFailureAudit/beta-results.json
node samples/WifiFailureAudit/pos-audit.cjs 'C:\Users\huang\Documents\Development\Entree POS\entree-pos' samples/WifiFailureAudit/pos-observed-results.json
```

The C# diagnostic instantiates the actual plugin `CommandProcessor`, durable `JobStore`, and `PrinterExecutionQueue`, injecting an `IPrinterBackend` that counts submissions. It verifies one submission after lost-ACK/restart replay, rejects payload conflicts, restores unsent work, and prevents a second submission after an uncertain error. Status probing is disabled and its background service is never started. Store recreation is not an OS process-kill test or proof of actual Windows completion. Alias/device serialization remains a release gate.

The Node diagnostic loads the current POS patch loader and print-status wrapper in an isolated VM with fake C-Lodop and audit callbacks. A fake accepted PRINT with no return callback becomes a reported failure. An explicitly simulated second operator attempt creates a new audit identity and submits again. It separately reproduces empty-queue reconciliation to `printed`. Timers are capped at 30 ms for the simulation; these results do not measure real Wi-Fi latency. Neither diagnostic reproduces RF interference or a specific field outage.

Current C# results are in [beta-results.json](beta-results.json). [observed-results.json](observed-results.json) and its source provenance are historical pre-fix evidence, not current results. The client fixture results remain in [pos-observed-results.json](pos-observed-results.json). The [release checklist](../../BETA_RELEASE_CHECKLIST.md) distinguishes reproduced behavior, implementation changes and field checks still needed.

When fixes land, convert the relevant cases into regression tests asserting the desired behavior. Do not keep a test suite that requires these defects to remain present. These diagnostic scripts will intentionally fail or need retirement once implementation behavior changes.
