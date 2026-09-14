# Entree Print

<img src="assets/entree-print.png" width="72" alt="Entree Print printer icon">

Clear receipt printing for web apps, tablets and desktop applications. Entree Print runs on Windows, provides a JavaScript API, and sends jobs through the installed Windows printer drivers. Entree POS is one client; the plugin does not require its order system or business logic.

**0.0.1-beta is in development.** The current source has passed a physical Chinese/English receipt and QR/Code128 scan check. Installer deployment, LAN transport and failure pilots remain release gates. The [usage guide](https://entreepos.github.io/entree-print/) walks through setup, connection, HTML preview, codes and recovery. Try editable receipt examples in the [playground](https://entreepos.github.io/entree-print/playground.html). A downloadable installer will accompany the release once its checks pass.

## What it does

- Prepares HTML receipts and returns HTML for your application's preview.
- Draws positioned text and vector QR/barcode graphics through Windows, using the driver's printable area and resolution.
- Supports installed USB, Ethernet and Wi-Fi printer queues. Configure the printer and paper in Windows.
- Saves accepted jobs before acknowledging them, keeps offline destinations queued, and tracks Windows spooler evidence.
- Checks request integrity and uses a stable intent key to detect duplicate submissions.
- Provides optional client-side storage for unsent receipts and recovery after a client restart.
- Includes a tray application for settings, service management and diagnostics.

General image input is excluded. Receipt HTML supports a defined subset of CSS; it is not a general browser-page printing API. Chinese and other text require suitable fonts on the Windows host. QR, Code128 and Code39 are supported; individual printer/driver combinations still need testing.

## Connect and print

Bundle the `sdk` directory with your application. This is currently a private development package, not an npm release.

```javascript
import EntreePrint from './sdk/entree-print.mjs';

const print = await EntreePrint.config({
  ip: '127.0.0.1',
  port: 9779,
  token: settings.printServiceToken
}).connect();

const printers = await print.getPrinters();
const ticket = print.target(printers[0].name).setContent([
  { type: 'html', html: '<h2>Order 1042 · 谢谢</h2><p>1 × Fried rice — $12.00</p>' },
  { type: 'qrcode', value: 'ORDER1042', sizeMm: 24 },
  { type: 'barcode', format: 'code128', value: 'ORDER1042', showText: true }
]);

const rendered = await ticket.render();
previewFrame.setAttribute('sandbox', '');
previewFrame.srcdoc = rendered.html;

// Persist a unique intent for this destination and intended copy before sending.
const job = await ticket.print({
  idempotencyKey: printIntent.id,
  metadata: { station: 'COUNTER-1', orderID: '1042' }
});
```

`connect()` returns the connected library. `getPrinters()` retrieves printer objects from it. The library remains bound to that server even if another top-level connection selects a different one. For network clients, `ip` is the Windows print-service computer's address, not the printer's address. Connection errors throw; there is no endless readiness loop.

Omit `setWidth()` to use the actual driver printable area. An 80 mm paper roll often has a narrower printable width. The verified POS-80C setup supplied 576 dots at 203 DPI, approximately 72.07 mm. An explicit width can narrow the content but cannot enlarge the driver's printable area.

## Discovery and status

```javascript
// Node / Electron main entry point; browser pages cannot broadcast UDP directly.
import EntreePrint from './sdk/native.mjs';

EntreePrint.config({ token: settings.printServiceToken });
const servers = await EntreePrint.search();
const print = await EntreePrint.search().connect();
const status = await print.target('cashier').status({ refresh: true });
const history = await print.getJobs('cashier', { limit: 30 });
const job = await print.getJob({ idempotencyKey: savedIntent.id });
```

Awaiting `search()` collects verified available servers. `search().connect()` selects the first successful connection. For configured primary/backup servers, [`connect({ nodes })`](sdk/README.md#connecting-through-a-backup-server) tries them in preference order and returns a library bound to the selected server. Automatic heartbeat checks the print service; printer status comes from Windows and may not expose paper or cover sensors. Same-service address recovery is implemented. Existing jobs never move automatically to a different server; routing during printing remains pending.

An accepted job means the plugin saved it. Windows completion is a separate observation and is not independent confirmation that paper emerged. Never create another intent key merely because a response was lost.

For a live print monitor, use [`EntreePrint.subscribe()`](sdk/README.md#resumable-status-updates). Job and printer updates resume after a connection loss; expired history triggers a current-state refresh. Synchronization events distinguish catching up from being current, and removed Windows queues are reported explicitly. Monitoring never resends tickets.

## Optional client outbox

The browser/Electron renderer SDK can retain unsent receipts in IndexedDB. Node/Electron main clients can use the optional [SQLite outbox](sdk/README.md#node-and-electron-main-storage). Call `enqueue()` before network submission, then `flush()` with the owning connected library. Each printer progresses in order; another printer can proceed independently.

```javascript
const outbox = EntreePrint.outbox();
await outbox.enqueue({
  idempotencyKey: savedIntent.id,
  serviceId: savedIntent.serviceId,
  printer: 'cashier',
  content: '<p>Order 1042 · 谢谢</p>'
});

const results = await outbox.flush(print);
const localIntents = await outbox.list();
```

Local `pending` and `accepted` are different states. Exact request bytes are saved before a job is submitted, so recovery can retry the same request without making another render. The outbox does not send in the background after the application closes: reconnect and call `flush()` when it is open again. Hard failures need operator attention. Browser storage requires IndexedDB and Web Locks, remains specific to the application's origin, and can be lost if the user clears that site's data. See the [SDK guide](sdk/README.md) for storage scope and recovery controls.

## Run the Windows application

Until the installer is ready, use a verified development package built from source. Follow [BETA_SETUP.md](BETA_SETUP.md) for runtime prerequisites, service configuration and removal.

1. Install your printer in Windows and select its paper size and printing preferences.
2. Open `tray/EntreePrintTray.exe`. Settings opens on a manual launch.
3. Generate an API access token, configure the application's website address if needed, and save.
4. Use the tray's **Windows Service** menu to install/start the service on your test host.
5. Configure the client with that service address and token.

The printer icon lives near the Windows clock and may be inside the hidden-icons arrow. Opening the tray EXE again brings Settings forward. The tray and Windows service are separate processes.

Current prerequisites are Windows x64, .NET/ASP.NET Core/Windows Desktop 10.0.12 or a later 10.0 patch, and Microsoft Edge or Google Chrome for layout preparation. Current service transport is HTTP; protected LAN transport and browser access are still under verification. Do not mistake the website origin list or a checksum for encryption.

## Develop and verify

Use the SDK selected by `global.json`, Node.js 22.13+ with `node:sqlite` enabled, and Edge or Chrome for the complete test suite. The optional SQLite adapter's runtime requirements are separate from the core SDK's requirements in `sdk/package.json`.

```powershell
dotnet test EntreePrintPlugin.Tests/EntreePrintPlugin.Tests.csproj --artifacts-path "$env:TEMP\EntreePrintTests"
node --test sdk/test/*.test.mjs
node --test sdk/test/native/sqlite-outbox.test.mjs
powershell -NoProfile -File scripts/test-package.ps1
powershell -NoProfile -File scripts/publish.ps1 -OutputRoot ./dist/my-beta-build
```

Publish into a new directory. Publication builds and verifies the package; it does not install the service or print a receipt.

| Reference | Contents |
| --- | --- |
| [SDK guide](sdk/README.md) | Working API examples and recovery behavior |
| [API implementation](API_V2_IMPLEMENTATION.md) | Implemented routes and current limits |
| [API design](API_DESIGN.md) | Full contract, with planned features identified |
| [Release checklist](BETA_RELEASE_CHECKLIST.md) | Evidence and outstanding gates |
| [Package verification](PACKAGE_VERIFICATION.md) | Build, printer and desktop checks |

## 0.0.1 release milestone

The [source repository](https://github.com/EntreePOS/entree-print), this README and the [usage guide](https://entreepos.github.io/entree-print/) are available. A self-contained development installer can now be built using the [installer instructions](https://github.com/EntreePOS/entree-print/blob/main/installer/README.md). It will be attached to the GitHub release after deployment and reliability checks pass; a successful source build alone does not satisfy the 0.0.1 milestone.
