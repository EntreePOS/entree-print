# ENTREE Print — 0.0.1-beta development

Standalone Windows print service for receipt HTML, Chinese and other Unicode text, QR codes, and Code39/Code128 barcodes. Web, tablet and desktop applications can use it; Entree POS is one client. All receipt and device-command output uses installed Windows queues. Receipts are measured by Chromium and drawn as positioned text/vector geometry through the driver; the API does not accept general images.

This is the first release, with no backward compatibility requirement. The supported HTTP surface is `/api`, with API version `0.0.1`. Prototype `/print`, `/sdk`, `/api.js`, `/events`, `/printers`, `/health` and `/v2/*` routes are removed. The old secret/salt-derived token and generated SDK are removed. Bundle the [SDK](../sdk/README.md) with the POS and configure its token explicitly.

See [API design](../API_DESIGN.md) for the full target contract and [implementation status](../API_V2_IMPLEMENTATION.md) for exact current routes and limits. The beta is not release-ready; the currently installed prototype has not been replaced.

## Configuration

The tray writes `C:\ProgramData\EntreePrintPlugin\entree-print-settings.json`. Set `ENTREE_PRINT_CONFIG` to override the path for both processes. Save and restart the service to apply changes. Named deployment overrides have priority, then flat tray settings, then bundled `Plugin:*` defaults.

- HTTP port defaults to 9779; UDP discovery defaults to 9778.
- `AccessToken` is the single API credential; generate it in Security. `ENTREE_PRINT_API_TOKEN` can override it for the service. All API routes except `/api/health` require a bearer token. An empty token disables authenticated operations.
- `CorsAllowedOrigins` is the POS website address list, including scheme/port without page paths. Separate origins with commas; `*` allows all and blank blocks cross-origin browser access. CORS is not authentication or a network firewall.
- Windows driver settings provide printable area and DPI. No duplicate printer profile is required for normal receipts. Optional `PrinterProfiles` entries now contain model-specific device commands only.
- Keep service identity and the durable job ledger together. Corrupt persisted identity fails startup rather than silently changing print servers.

The service defaults to HTTP and supports optional HTTPS using a certificate from Local Computer / Personal. See [HTTPS setup](../BETA_SETUP.md#https-setup); invalid TLS configuration stops startup without HTTP fallback. Installed certificate access and browser/tablet deployment remain release gates. The browser SDK needs Web Crypto and is subject to browser secure-context/mixed-content rules. Configure printers in Windows. The production backend prints the saved layout; screenshot/PDF fallback, delivery-time HTML rendering, obsolete block/ESC/POS receipt renderers and their settings have been removed. Historical image comparison code is isolated in the comparison sample.

## Printing and recovery

`render()` returns prepared HTML without printing. `print()` resolves after durable plugin acceptance, not after paper comes out. Requests are validated for exact UTF-8 SHA-256, schema, token and service identity before new job acceptance. Reuse the original key and payload when the response is lost.

Windows handoff records the actual spooler job ID and unique attempt document. Notifications and GetJob reconciliation track that exact job. Positive completion plus full handoff can establish **Completed by Windows**, not independent physical paper confirmation. Missing completion evidence becomes `needs_attention`; an uncertain handoff is never automatically resubmitted.

Accepted receipts are saved on disk before acknowledgement. Each printer has an independent queue. A fresh offline, paper-out, open-cover or paused observation keeps its receipts in `waiting_for_printer` until the condition clears, without using up delivery attempts. Unchanged observations do not rewrite the receipt repeatedly. Restart restores unsent jobs in order; completed jobs are not sent again. After Windows accepts a job, monitor that Windows job rather than submitting another copy. Proven pre-submission backend failures still use the bounded retry policy; uncertain handoff requires attention.

Status queries share a bounded refresh and preserve timestamps. Unreported sensors remain unknown; a printer listing or successful service heartbeat does not prove paper availability. Real printer/driver fault and scanner validation remain required.

Long receipts paginate at the Windows driver's printable height without shrinking. Text lines, generated QR/barcodes and HTML sections using `break-inside: avoid` stay together. An indivisible section taller than a page fails before submission. All pages share one Windows job ID. The preview remains a continuous receipt. A 120-item/four-page Microsoft PDF job completed with all pages visually checked and both codes decoded; physical thermal feed/cut behavior still needs a pilot.

Code origins and edges align to whole driver dots in the saved preview and printed pages. Encoder module widths are retained instead of being independently rounded from browser geometry. Fractional receipt widths are applied to the HTML root so right alignment uses the selected physical width. The latest virtual PDF check is job 21, with the same four-page/120-item outcome and successful QR/barcode decoding.

Printer status comes from Windows only. The automatic direct TCP ESC/POS probe and its settings have been removed; the plugin does not open a separate printer connection for status. Windows query output uses UTF-8 to preserve Chinese and other non-English queue names. A read-only development-user query found seven Windows queues, including one reported offline, with no print jobs or direct device commands.

## Development verification

```powershell
dotnet test EntreePrint.sln
node --test sdk/test/*.test.mjs
```

The current suite passes 416 .NET tests, 115 core SDK tests and nine native SQLite tests locally. Nine integration scenarios run the actual SDK against ASP.NET TestServer, real Chromium preparation and the durable ledger. They cover lost acknowledgements, independent offline destinations, replay, events and ordered actions. TLS tests use Kestrel with memory transports, including certificate rejection. Windows endpoint identity, API/event consistency and backup inventory revalidation have controlled coverage. Browser and native storage tests cover reload/process exit, concurrent dispatch and retained uncertainty after later rejection. Prior [virtual spooler evidence](../samples/BetaSmoke/README.md) and [process termination recovery](../samples/RecoverySmoke/README.md) retain their stated scope. Exact tested source and installer evidence are recorded in [PACKAGE_VERIFICATION.md](../PACKAGE_VERIFICATION.md).

Durable job/printer events and ordered receipt actions are implemented. Configured nodes support initial backup selection; automatic routing during printing remains unfinished. Remaining release work includes live LAN/discovery/resume and spooler recovery, transport protection, service-account driver access, long-receipt feed/cut checks and interactive installation/logon. Long-term history export and client integration work are tracked separately. Entree POS migration is not a prerequisite for this standalone product. See [BETA_RELEASE_CHECKLIST.md](../BETA_RELEASE_CHECKLIST.md) for current requirements and evidence.
