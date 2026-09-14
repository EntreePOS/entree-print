# SDK and service contract checks

Run from the repository root with Node.js 22.13+ (`node:sqlite` enabled) on PATH and the project's .NET SDK installed:

```powershell
dotnet test EntreePrintPlugin.Tests/EntreePrintPlugin.Tests.csproj --artifacts-path "$env:TEMP\EntreeSdkServiceIntegration" --filter FullyQualifiedName~SdkServiceIntegrationTests
```

The .NET runner starts `service-contract.mjs` as an ordinary Node child process. The actual `entree-print.mjs` SDK sends exact UTF-8 request bytes and headers through a stdio fetch bridge into ASP.NET TestServer. Responses include the actual service status, JSON and headers. No HTTP/UDP listener, installed Windows service or physical printer is used.

Production routing, authorization, request checksum/schema validation, Chromium layout preparation, saved HTML preview, job acceptance, file-backed ledger, per-printer execution queue and history/reprint handlers are exercised together. Driver measurements and printer inventory/delivery are fixtures. Restart closes and reopens the API and ledger at the same temporary path, preserving service identity and producing a new boot identity; it is not an OS process-kill or Windows-service restart test.

Scenarios:

- Lose both acknowledgements after acceptance, retain unknown outcome and identical SDK retry bytes, reopen storage after preview expiry, recover the original job without another delivery, then explicitly reprint once with a new key. Preview and Chinese metadata survive.
- Complete one destination while a Chinese-named kitchen queue is offline. The kitchen job waits with zero attempts across restart, then completes after the simulated queue returns. Replaying either intent must not create another job.
- Alter the request bytes without altering its checksum, then send invalid acknowledgement digests after acceptance. The invalid request creates no job; uncertain acknowledgements retain the original intent and recovery creates no duplicate delivery.
- Complete a receipt, advance the service clock beyond retention, compact its layout and reopen the service/ledger. The actual SDK replays the original bytes to the same completed job; history remains, preview/new reprint return `ARTIFACT_EXPIRED`, and the backend is called only once.
- Save only the intent key and service ID to a flushed temporary client checkpoint before printing, lose both acknowledgements, expire the layout and reopen the service. Disconnect the old SDK, create a new SDK instance and recover the completed job from that checkpoint with `getJob({ idempotencyKey })`. No additional render or submission occurs and backend delivery stays at one. This models lost SDK memory; it does not kill the Node process or implement the POS outbox.
- Enqueue unsent content in a flushed file-backed client fixture before connecting, replace the SDK, submit through the production API and lose both acknowledgements. Expire the accepted layout, reopen service storage, replace the SDK again and flush the original persisted request. The bytes remain identical, Chromium prepares once, and the backend receives one receipt. The file adapter is test-only; the production browser adapter uses IndexedDB and Web Locks.
- Repeat that outbox scenario with the production SQLite adapter, reopening its store for each new SDK. The saved wire survives expired layouts and service ledger reopen; recovery returns the original completed job with one backend delivery. Separate native tests kill a real lock-owning Node process and verify lock release plus retained committed wire; the TestServer scenario itself does not kill a process.

The integrated status lookup exposed a path-encoding bug for `/` in queue names. Status now uses `GET /api/printers/status?printer={encodedName}&refresh=true`. Additional handler tests cover Chinese/slash names, literal percent sequences, ampersands, plus/hash characters, UNC-style names, and missing/duplicate query values.

This verifies SDK/service interoperability under simulated transport faults. It does not establish live HTTP/TLS, Wi-Fi, discovery, spooler or hardware behavior. The separate Node unit suite remains `node --test sdk/test/*.test.mjs`.
