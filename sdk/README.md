# Entree Print SDK — 0.0.1-beta development

This is the SDK for the fresh 0.0.1 API under `/api`. There is no prototype API compatibility layer. Bundle the SDK directory with your application; `entree-print.mjs` imports `outbox.mjs` and `events.mjs`. It contains no installation token and does not download executable code from the print service. Entree Print is standalone; Entree POS is one possible client. The package is private while release verification remains incomplete.

```javascript
import EntreePrint from './entree-print.mjs';

const print = await EntreePrint.config({
  ip: '127.0.0.1',
  port: 9779,
  token: settings.printServiceToken
}).connect();

const printers = await print.getPrinters();
const ticket = print.target(printers[0].name)
  .setContent([
    { type: 'html', html: '<p>欢迎 · Order 10086</p>' },
    { type: 'qrcode', value: 'ORDER10086', sizeMm: 24 },
    { type: 'barcode', format: 'code128', value: 'ORDER10086', showText: true }
  ]);

const rendered = await ticket.render(); // No printer output.
previewFrame.setAttribute('sandbox', '');
previewFrame.srcdoc = rendered.html;

// On the operator's print action, use a key already persisted with the POS intent.
const job = await ticket.print({
  idempotencyKey: printIntent.id,
  metadata: { station: 'POS-1', orderID: 'order-10086' }
});
// job means durable plugin acceptance. It does not prove physical paper output.
```

Ordinary receipt printing uses the Windows driver's printable area and resolution automatically; configure the printer in Windows, not in Entree Print. Omit `setWidth()` to use that area. An explicit width only narrows content and cannot exceed the driver area. A string passed to `setContent()` is HTML; arrays contain `html`, `qrcode`, and `barcode` blocks. General images are rejected. Optional model-specific device commands remain separate from ordinary receipt printing.

`connect()` returns a connected print library. Retrieve the printer list with `await print.getPrinters()` (or `{ refresh: true }` for a fresh Windows query). The returned object exposes `target`, `getPrinters`, `getJobs`, `getJob`, `getJobRender` and `reprintJob`. Its `connection` property returns a snapshot of server details without a printer list or credential. The library stays bound to its selected service even if a later top-level connection selects another server. Connection management (`config`, `search`, `disconnect`, `resume`, `subscribe`) remains on `EntreePrint`; `disconnect()` stops all its sessions. Top-level printing methods still use the currently selected service.

Direct `connect(options)` and `search().connect()` return the same library interface. The library code is bundled with the POS; the service handshake returns data, and the SDK returns the configured callable object. Connection still throws when the service is unavailable or has no installed printers.

`render()` also returns `comparisonId`: an opaque ID for the prepared layout, driver measurements and local rendering/font inputs, or `null` when comparison is unavailable. Check `warnings` for that limitation. This metadata does not transfer a render or authorize backup printing; render IDs and accepted jobs remain owned by their original service. The service rechecks available comparison evidence at new acceptance and before Windows submission. Matching IDs do not guarantee identical physical output or lock settings/font files during drawing. Ordinary rendering and printing remain available when comparison is unavailable.

## HTTPS connections

The service can use HTTPS on its configured port (9779 by default). Follow [certificate setup](../BETA_SETUP.md#https-setup) on the Windows service computer, then connect with the certificate's DNS name or IP address:

```javascript
const print = await EntreePrint.connect({
  ip: 'print.example.com',
  port: 9779,
  protocol: 'https',
  token: settings.printServiceToken
});
const printers = await print.getPrinters();
```

Use a name that resolves to the service computer and appears in the certificate's Subject Alternative Names. Every client must trust its issuer. Certificate errors remain connection errors; the SDK does not disable validation. HTTPS discovery and same-service address recovery ignore HTTP announcements, and HTTP and HTTPS libraries keep separate connection monitors. Explicitly configured backup nodes retain their own `protocol` settings; set every node to `https` for an encrypted deployment.

Native UDP discovery verifies the actual sender IP, so its certificate must include that IP as an IP Subject Alternative Name. A DNS-only certificate works for a direct DNS connection but does not make IP discovery work. The playground checks HTTP and HTTPS at `127.0.0.1:9779`; its HTTPS connection requires a trusted certificate with a `127.0.0.1` IP Subject Alternative Name. When the plugin is unavailable or authorization is missing, receipt editing remains available in preview mode.

HTTPS does not replace the access token or permitted website settings. Browser network-access policies still apply. Installed certificate/private-key access, renewal and browser/tablet deployment remain live pilot checks.

## Implemented operations

- `config()`, `connect(options?)`, `disconnect()`, `target(name)`, awaitable `search()` and `search().connect()` with a native discovery transport. `connect({ ip, port, token })` accepts settings directly; `connect(server)` also accepts a record returned by `search()` and verifies its service ID. Omitted settings retain configured defaults. Existing targets keep their original service.
- Immutable `setContent()` and `setWidth()` builders, `render()` and `print({ idempotencyKey?, metadata?, after? })`.
- `target.status()`, `beep()`, `openDrawer()`, `cut()`, `sendCommand(Uint8Array)`; optional `idempotencyKey`/`metadata` on each command.
- `getPrinters({ refresh })`, `getJob(id)`, `getJob({ idempotencyKey })`, `getJobRender(id)`.
- `getJobs(printerName?, { station?, orderID?, status?, since?, limit?, cursor? })` returns `{ items, nextCursor }`; default 30, maximum 100. `status` accepts one state or an array. `since` is an ISO date/time with a time-zone offset or `Z`.
- `reprintJob(id, { idempotencyKey })` requires a new, persisted operator-action key and returns the linked receipt job.
- `subscribe(handler, { events? })`, returning `{ close() }`. Defaults to connection, printer, job and sync events. Use `{ events: ['connection'] }` for heartbeat-only monitoring.
- Automatic service heartbeat after connect; `resume()` performs an immediate check. Browser pageshow/focus/visibility hooks trigger a fresh check and inventory refresh; Electron can supply its powerMonitor resume event through the native factory below.

### Receipt actions in order

```javascript
const job = await print.target('kitchen')
  .setContent('<h2>Order 1042</h2><p>炒饭 × 2</p>')
  .print({ idempotencyKey: savedIntent.id, after: { beep: true } });

// Later, through an event or a status read:
const current = await print.getJob(job.id);
const beep = current.after?.beep;
```

`after` accepts boolean `cut` and `beep` flags. The order is receipt → optional raw cut → optional beep, regardless of object-key order. Drawer opening and arbitrary raw bytes are separate explicit actions. A new receipt validates all requested actions before durable acceptance. Missing/unsupported device bytes reject the whole request without submitting the receipt. For ordinary driver-managed cutting, omit `after.cut`; a raw cut requires `CutMode: "raw"`, verified `CutCommandHex` and driver cutting disabled in Windows. The default cut policy is `driver`.

The receipt and actions share one intent and one per-printer queue position. Other Entree Print jobs cannot enter between their submissions. Windows scheduling, its driver and other applications can still affect downstream processing; this is not physical execution confirmation. Each action has independent Windows identity and state in `job.after.cut` / `job.after.beep` (`state`, `version`, `reason`, `attempts`, `delivery`, `spooler`). A skipped action has state `skipped` and was not sent. Root `spooler`/`delivery` still describe the receipt. Overall `job.state` becomes `needs_attention` if an action fails or is uncertain, while the receipt's completion evidence remains intact.

Retries preserve the same receipt and action bytes; changing actions under the same key conflicts. After restart, only actions known to be unsent may continue following a confirmed full receipt handoff. An uncertain action is never automatically repeated, and later actions are skipped. A storage failure holds the plugin queue position until phase state can be saved or the service stops. Receipt layout retention waits for all actions to complete and starts from the last completion; unresolved action jobs retain their layout. An explicit `reprintJob()` copies the original actions as well as its receipt and metadata, and is rejected while any phase remains active in Windows.

The client outbox also accepts `after`; it saves the flags with source content and then retains the exact receipt request. It does not create independent device-command retries. Automatic backup selections require a direct owner connection for receipts with device actions, consistent with standalone helper commands.

### Resumable status updates

```javascript
const subscription = EntreePrint.subscribe(event => {
  if (event.type === 'sync') {
    // Initial connection/history gap: replace the complete printer inventory.
    if (event.data.printers) monitor.replacePrinters(event.serviceId, event.data.printers);
    monitor.setSyncState(event.serviceId, event.data.state);
  }
  if (event.type === 'printer') {
    if (event.data.removed) monitor.removePrinter(event.serviceId, event.entityId);
    else monitor.updatePrinter(event.serviceId, event.data);
  }
  if (event.type === 'job') monitor.updateJob(event.serviceId, event.data);
}, { events: ['printer', 'job'] });

// When this view/application no longer needs status updates:
subscription.close();
```

Subscriptions observe verified connected services in this SDK instance. Equivalent aliases share one stream when their credentials and heartbeat policies match. Adding a remote subscriber refreshes the shared snapshot so the new view receives current state. Closing the last remote subscriber stops the stream; heartbeat monitoring continues until `disconnect()`. Callback exceptions and mutations are isolated between listeners.

Remote subscriptions also receive `sync` lifecycle events, even when the filter lists only printer/job events: `synchronizing`, `replaying`, `live` and `reconnecting`. Connection `online` describes service reachability; `sync: live` means the status stream has caught up. Do not mark the monitor current from heartbeat alone. A synchronization-start event carries the **complete** printer list, including an empty array after all queues are removed. Job snapshot entries have `snapshot: true`, `id: null` and their current job version. Printer snapshot entries have `snapshot: true`, `id: null`, `version: null`. These are current observations, not fabricated durable events.

Live events have `{ id, serviceId, type, entityId, version, occurredAt, data }`. The SDK sends the service-owned cursor in `Last-Event-ID` and the access token only in the authorization header. It detects malformed/foreign/gapped events and never advances past them. It reconnects with bounded backoff (up to 30 seconds) and a 30-second silence deadline. Repeated job versions and already acknowledged sequence IDs are suppressed. Removed queues emit a printer event with `{ name, removed: true }`.

On initial subscription or expired history, the SDK obtains a locked printer snapshot/cursor, pages **unfiltered** jobs in stable acceptance order, then replays from that earlier cursor. Job versions prevent older replay from overwriting newer page observations. This converges on current state; pagination is not an instantaneous frozen job snapshot. If the event window expires during recovery, the SDK repeats recovery without skipping the gap. Consumers that also issue their own job reads should compare job versions before updating shared state. Printer sensor values can be unknown, and observations age even if no new event arrives; use `status.observedAt` and connection freshness.

Recovery only reads state. It never flushes an outbox, renders, prints, cancels, rekeys or moves a job. Cursors are in-memory within the SDK; a new SDK instance reconstructs current state from the service. Separate POS audit/export storage remains application-owned. Controlled tests and an actual SDK/TestServer replay across storage reopen cover this contract; real LAN interruptions and OS service restarts remain beta verification work.

## Connecting through a backup server

```javascript
const print = await EntreePrint.connect({
  nodes: [
    { serviceId: settings.primaryServiceId, ip: '192.168.1.10', port: 9779, token: settings.primaryToken },
    { serviceId: settings.backupServiceId, ip: '192.168.1.11', port: 9779, token: settings.backupToken }
  ],
  timeoutMs: 10000,
  failover: true
});
const printers = await print.getPrinters();
console.log(print.connection.serviceId, print.connection.nodeSelection.usedBackup);
```

This selects one server during `connect()`. Supply 1–8 distinct servers in preference order, each with its own explicit token, address and expected service ID. The SDK validates every entry before contacting any server, verifies the responding identity and requires an installed printer. The total deadline defaults to 10 seconds; silent candidates leave time for later candidates. `failover: false` tries only the first server. If none succeeds, connection rejects with `SERVICE_UNAVAILABLE`; `error.details.nodes` records the attempted addresses and error codes without credentials.

The returned library is bound to the selected server. `connection.nodeSelection` contains `selectedServiceId`, `usedBackup`, and candidate states (`selected`, `failed`, `not_checked`) from that connection attempt; it is not a live health report for unused nodes. Retrieve installed queues with `getPrinters()`; there is no `printers` configuration mapping. Configure each eligible Windows queue directly to the intended Ethernet printer. An automatically selected backup requires a non-stale, comparable Windows TCP/IP destination. Before submitting, the SDK refreshes Windows inventory and requires exactly one queue with the original destination ID. A changed endpoint returns `PRINTER_DESTINATION_CHANGED`; missing, ambiguous, stale or unidentified destinations return `PRINTER_OWNER_REQUIRED`. No job POST occurs on that failed check, and any earlier possible acceptance remains uncertain. Connect directly to the device owner for drawer, beep, cut or raw commands (`DEVICE_OWNER_REQUIRED`) and to the recorded job owner for reprints (`JOB_OWNER_REQUIRED`). These restrictions also cover the durable outbox.

### Windows destination information

Each printer's `connection.destination` is either `null` or an object with `id`, `protocol`, `host`, `port` and `queue`. `protocol` is `raw` or `lpr`; `queue` is the exact LPR queue name, or `null` for RAW. The `tcpip:` ID identifies that configured endpoint across servers and differently named Windows queues. Treat it as an opaque value. It excludes queue name, Windows port name and driver/layout settings.

The service derives this from a local Windows queue using the standard TCP/IP port monitor and a literal unicast IP address. Shared/remote queues, USB, pooled ports, custom monitors, DNS addresses, scoped/link-local IPv6, missing data and unsupported protocols have no comparable destination. They remain available for ordinary printing through an explicit owner connection; the plugin neither reconfigures their driver nor probes their network address. Microsoft documents the [RAW/LPR protocol and queue fields](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-tcpipprinterport).

Matching IDs establish matching endpoint configuration, not a printer serial number, physical output or equivalent fonts/paper layout. Reassigned IP addresses and different network segments still need deployment verification. The SDK's refresh is a pre-submission observation; it cannot prevent an administrator changing a Windows port afterward. Automatic matching/routing between nodes and portable-preview validation remain unfinished. Clients do not need to supply a printer mapping or duplicate Windows settings.

Heartbeat recovery still finds the same service at a new address. It does not select another server or transfer jobs. Existing libraries, targets, prepared tickets and outbox records retain their original owner after another `connect()` call. Do not catch a print timeout and recreate the job on a backup: its original server may already have accepted it, and each server has its own deduplication ledger. Automatic routing during printing and coordinated cross-node ownership remain unimplemented.

## History and explicit reprints

```javascript
const filters = { station: 'POS-1', orderID: 'order-10086', limit: 30 };
const page = await EntreePrint.getJobs('Kitchen', filters);
const nextPage = page.nextCursor
  ? await EntreePrint.getJobs('Kitchen', { ...filters, cursor: page.nextCursor })
  : null;

// Only after an operator requests another copy; persist this action key first.
const copy = await EntreePrint.reprintJob(originalJob.id, {
  idempotencyKey: reprintAction.id
});
```

History sorts by acceptance time, then job ID, descending. Reuse the same filters with a cursor; it belongs to one service. Page size may change. Newer jobs do not shift later pages; status filters are evaluated live, so this is not a frozen snapshot. List items contain metadata and render IDs, without receipt HTML. Use `getJobRender(id)` for the retained preview.

`print.connection.retention` describes the server policy. Finished receipt layouts are retained for seven days by default (service configurable); pending and uncertain receipts do not expire. Jobs report `artifactExpiresAt` and `artifactExpiredAt`. After cleanup, previews and new reprints return `ARTIFACT_EXPIRED`, while history remains. The original exact print/reprint request still returns its retained job without printing again. Changed request bytes for a compacted key return `IDEMPOTENCY_CONFLICT`; keep the original bytes for uncertain retries. Intent IDs are never silently deleted to make room: full storage rejects new work with `QUEUE_FULL`.

A reprint preserves the original destination, metadata and prepared layout, even after its preview TTL expires. It requires the same printer profile and a retained artifact. Active jobs and device commands return `JOB_NOT_REPRINTABLE`. For `needs_attention`, show the uncertain outcome before requesting another copy: a reprint does not cancel any remaining Windows job. Its `reprintOf` links the original, whose evidence stays unchanged. Retry a lost acknowledgement with the same original ID and action key; never generate another key for that retry.

## Retry and connection behavior

Each ticket keeps its own content snapshot, shared preparation promise and generated default key. Repeated printing of that unchanged ticket returns the same job. Concurrent calls share a submission. Explicitly supplied metadata is copied immediately; a changed payload with the same key conflicts. A helper invocation represents a new operator action unless the caller supplies an existing key.

Requests use exact UTF-8 byte SHA-256 and require an echoed digest plus the correct service/key in the acknowledgement. Automatic job retries reuse the original bytes/key/render ID. The default is one retry, with a 250 ms delay; `retryCount` supports 0–3. Render preparation is not silently retried. `requestTimeoutMs` defaults to 10 seconds and `renderTimeoutMs` to 30 seconds.

An `ACK_INVALID` or transport failure after submission reports `delivery: 'unknown'`; its `details` includes `serviceId` and `idempotencyKey`. Retain and retry that intent. Automatic retries preserve uncertainty from earlier attempts, including device commands and reprints. A ticket retains that evidence across later `print()` calls: neither a later rejection nor a new preview can replace its uncertain request's bytes. A server `RENDER_EXPIRED`, `RENDER_CHANGED` or `PRINTER_SETTINGS_CHANGED` rejection with `delivery: 'not_sent'` requires explicit `render()` and review before another print attempt. The SDK discards that invalid preview only when the intent has no earlier possible acceptance. A failed render or an older render that finishes late cannot clear this requirement; concurrent fresh render calls share preparation. `print()` itself never silently refreshes a reviewed preview. This ticket evidence is in memory; use the durable outbox across client restarts. Separate ticket objects, explicit command calls and clients must retain their original intent/owner evidence in application storage; this is not a shared cross-client claim.

Ordinary ticket builders keep generated keys and cached bytes in memory. Use the optional client outbox below, or persist intents in your application's own transactional storage, for reload recovery. Wiring this into Entree POS's checkout and audit UI remains client integration work. Never assign a fresh key because the response was lost.

If the POS saved its intent key but lost the returned job ID, reconnect to the saved owning service and look up that key:

```javascript
const print = await EntreePrint.connect({
  ip: savedIntent.ip,
  port: savedIntent.port,
  serviceId: savedIntent.serviceId,
  token: settings.printServiceToken
});
const job = await print.getJob({ idempotencyKey: savedIntent.id });
```

This is a read-only lookup. It neither renders nor submits a receipt and remains available after the accepted layout expires. Keys are exact and case-sensitive, with 1–200 characters and no all-whitespace value. Lookup errors, including `JOB_NOT_FOUND`, carry `delivery: 'unknown'`: an earlier request may still be in flight. A missing result is not permission to generate another key or send to a different server. This recovers accepted work; it does not store or deliver a POS request that never reached the plugin.

## Durable client outbox

`EntreePrint.outbox()` returns a shared controller for that SDK instance. In browser/Electron renderer contexts it uses IndexedDB and Web Locks. It does not contact a printer or service until `flush(print)` is called. Save the owning service identity in your application's connection settings after its first successful connection.

```javascript
const outbox = EntreePrint.outbox();
const local = await outbox.enqueue({
  idempotencyKey: savedIntent.id,
  serviceId: savedIntent.serviceId,
  printer: 'cashier',
  content: '<p>Order 1042 · 谢谢</p>',
  metadata: { orderID: '1042' }
}); // local.state === 'pending': stored on this client, not accepted by the plugin.

const print = await EntreePrint.connect({
  ip: settings.printHost,
  port: 9779,
  serviceId: savedIntent.serviceId,
  token: settings.printServiceToken
});
const results = await outbox.flush(print);
const localHistory = await outbox.list();
```

`enqueue()` snapshots content and metadata immediately, awaits storage commit, and returns the existing local record when the same service/key/content is repeated. Changed content, destination or metadata for that key returns `OUTBOX_CONFLICT`. Content accepts the same HTML string or HTML/QR/barcode block array as `setContent()`; optional `widthMm` narrows the driver area. This path prepares queued source content at dispatch time. Use `ticket.render()` followed by that ticket's `print()` for an operator-reviewed, already prepared preview.

`flush()` processes only the connected library's service. It serializes each destination across tabs using Web Locks while allowing other printers to proceed. It saves the prepared exact request before any job POST and preserves that request across lost ACKs, application restarts and service ledger reopen. Storage failures stop that destination before an unsaved request can be sent. If storing an ACK fails, the retained request can be retried without changing its key. `accepted` means the plugin acknowledged acceptance; inspect `record.job` for its Windows evidence. It does not mean paper output.

Local states are `pending`, `prepared`, `submitting`, `uncertain`, `needs_attention`, `accepted`, `cancelled` and `stopped`. A retryable preparation failure remains pending. Before each job POST, the outbox durably marks possible acceptance. If the client exits or cannot save its acknowledgement, later recovery keeps that evidence. A later rejection cannot clear an earlier possible acceptance, even after reopening SQLite/IndexedDB or explicitly retrying. The record stays `uncertain` with its original bytes; the next flush reuses them on the same owner. A hard rejection with no earlier possible acceptance instead requires attention. Both block later items for that destination. `outbox.retry({ serviceId, idempotencyKey })` explicitly enables another attempt with the same bytes; it does not regenerate an expired render. `outbox.cancel({ serviceId, idempotencyKey })` is allowed only before a request has been saved for submission. It cannot cancel a possible Windows/plugin job. Accepted/cancelled keys remain retained, so another intended copy needs its own persisted action key.

If an operator handles an unresolved ticket separately, `outbox.stopRetrying({ serviceId, idempotencyKey })` marks that local intent `stopped` and lets later tickets proceed. It retains the saved request and uncertainty; it neither cancels an in-flight service/Windows job nor proves that it did not print. Show that distinction before offering this action. A stopped intent is not revived by enqueueing its key again; an explicit `retry()` restores the same request. Do not automatically stop unresolved tickets to clear a queue.

The outbox handles receipt jobs only. It does not automatically replay drawer, beep or raw commands. It does not run after the application closes, subscribe to heartbeat or transfer ownership to another server. Call `flush()` after reconnect/startup or on your application's retry action, and display unresolved records. A flush is not an atomic batch: another printer can accept its work even if one destination's storage fails.

Storage is scoped to this application origin and capped at 10,000 local records; full storage returns `OUTBOX_FULL` without evicting old keys. Records currently retain content, wire and acceptance evidence; automatic local pruning/export remains unfinished. Storage commits request strict IndexedDB durability and wait for transaction completion. Older engines may ignore that hint; no universal power-loss guarantee is claimed. Clearing site data removes this local history. Locks coordinate tabs on the same origin, not different tablets. See [IndexedDB transaction durability](https://developer.mozilla.org/en-US/docs/Web/API/IDBDatabase/transaction) and [Web Locks](https://developer.mozilla.org/en-US/docs/Web/API/Web_Locks_API).

### Node and Electron main storage

Use the optional SQLite adapter in a runtime with [`node:sqlite`](https://nodejs.org/api/sqlite.html) enabled (Node 22.13+; tested on Node 24.19). Electron must provide that module in its embedded Node runtime. Importing the normal native SDK does not load SQLite or create storage.

```javascript
import { join } from 'node:path';
import { createSqliteOutboxStorage } from './sqlite-outbox.mjs';
import { createNativeEntreePrint } from './native.mjs';

// In Electron, applicationDataDirectory can be app.getPath('userData').
const storage = await createSqliteOutboxStorage({
  directory: join(applicationDataDirectory, 'print-outbox')
});
const EntreePrint = createNativeEntreePrint(settings.connection, { outboxStorage: storage });
const outbox = EntreePrint.outbox();
// Use the same enqueue/flush/retry methods shown above.
```

Choose an application-owned directory on a **local disk**, accessible only to the application's trusted Windows account. Network shares, synced folders and copied databases are not supported for shared dispatch. Every local process must use the same directory. The store retains intent keys, source, exact wire and ACK evidence across process restarts. It caps records at 10,000 and rejects new work when full without evicting history. Corrupt or unsupported databases fail without being reset. Keep the database and any SQLite journal files together; do not delete individual files to repair or unlock it.

Writes use short SQLite transactions with `synchronous=FULL`. A temporary reader lock can block `COMMIT` after write-lock acquisition; storage retries only that commit, without applying the update again. Acquisition and commit share the `lockTimeoutMs` budget. Timeout rolls back the pending update and preserves earlier committed request/uncertainty data; no successful storage acknowledgement is returned before commit. See [SQLite transaction semantics](https://www.sqlite.org/lang_transaction.html). Each destination has a separate SQLite lock file, so the process can commit request bytes before submitting while retaining its dispatch lock. OS locks release on process exit; they do not expire while a suspended process might still submit. A busy destination returns `OUTBOX_BUSY` after the configured `lockTimeoutMs` (default 10 seconds), without affecting another destination. This coordinates processes on one local machine, not different tablets or print servers. SQLite's [locking and journal recovery](https://www.sqlite.org/lockingv3.html) depend on the local filesystem; process-exit tests are not proof against every disk or power failure.

Call `storage.close()` after awaited outbox work finishes; it rejects closure during active operations. SDK `disconnect()` stops connections but does not close application-owned storage. Automatic pruning/export and background dispatch after the application closes remain unimplemented.

Applications may alternatively pass their own transactional `outboxStorage` to either factory. Required methods are `list`, `add`, `update(previous, next)` and `withQueue(serviceId, printer, work)`. Inserts must atomically deduplicate `(serviceId, idempotencyKey)`, updates must compare versions, and queue locks must span all dispatchers sharing that storage. Do not substitute an in-memory map or unlocked JSON writes for durable storage.

Targets retain the service identity selected when created. Reconfiguring the default cannot send an existing ticket to another service. A different service occupying that address is rejected; verified recovery may update its endpoint for the same identity. Within one SDK client, identical configurations reuse a session and concurrent handshake. Verified aliases of the same service share one heartbeat/recovery monitor when their token, request deadline and heartbeat policy match. Different credentials or monitoring policies remain separate. Each caller gets its own connection-result copy.

Each monitor uses a fresh heartbeat request ID, never overlaps its polls, reports `degraded` then `offline` after three misses, and rechecks identity/inventory before returning `online`. Connection events include `printersStale`; they never manufacture a printer-offline or paper-out observation. Defaults are 5-second intervals and 2-second deadlines, configurable with `heartbeat: { intervalMs, timeoutMs, missedLimit }`. A verified address change updates every member while preserving each ticket's render, bytes and key. Older delayed handshakes cannot replace a newer address. Disconnect aborts sessions and clears monitor/timer/lifecycle registrations. Sharing does not extend across separate `createEntreePrint()` instances or POS processes and is not cross-server failover.

`disconnect()` aborts SDK requests and stops timers across its sessions. It does not cancel accepted jobs. Call `connect()` explicitly before further operations. Closing a subscription removes only its callback.

## Runtime and tests

Use a modern browser/Electron/Node runtime with Fetch, TextEncoder, AbortController and Web Crypto. Browser Web Crypto normally requires a secure context, and browser mixed-content/private-network rules still apply. In Node/Electron, use `createEntreePrint(options, { crypto: webcrypto })` with `webcrypto` imported from `node:crypto` when a global crypto implementation is unavailable. No fallback random IDs or weaker checksums are used.

`createEntreePrint()` creates an independent client for applications that need multiple separate configurations. Its optional second argument also accepts `fetch`, `now`, `setTimeout`, `clearTimeout`, `discover` and `subscribeResume` for controlled tests/native integration. `subscribeResume(handler)` must return a cleanup function. The discovery callback receives `{ signal, timeoutMs, serviceId?, onCandidate }`, reports `{ ip, port, serviceId?, protocol?, name? }` candidates, and returns a promise for transport completion. Cancellation must close native resources.

```powershell
node --test sdk/test/*.test.mjs
# Optional native storage checks require node:sqlite.
node --test sdk/test/native/sqlite-outbox.test.mjs
```

95 core SDK tests and nine SQLite outbox tests pass, including controlled transport/discovery/heartbeat checks, real Chromium IndexedDB/Web Locks, local process crashes and concurrent native dispatch. Nine SDK/TestServer scenarios also run in the .NET suite, including the production SQLite adapter, resumable status events and ordered receipt actions with real Chromium preparation, ledger reopen and lost acknowledgements. Real LAN discovery, OS sleep/resume and full installed-service network/physical-printer integration remain pending. Browser storage tests require Node.js 22+ and local Edge/Chrome; the SQLite scenario requires node:sqlite enabled.

## Discovery and address recovery

In Node or Electron's trusted main process, use the native entry point:

```javascript
import EntreePrint from './native.mjs';

EntreePrint.config({ token: settings.printServiceToken });
const servers = await EntreePrint.search();
// [{ serviceId, ip, port, protocol, name }, ...]

const print = await EntreePrint.search().connect();
const printers = await print.getPrinters();
// First compatible service whose authorized connection/inventory handshake succeeds.
```

For Electron resume support or a different discovery port, use `createNativeEntreePrint(options, { discoveryPort: 9778, powerMonitor })` from `native.mjs`, passing Electron's `powerMonitor`. Do not import the UDP entry point in an untrusted/browser renderer. A POS integration bridge between the renderer and trusted main process remains project-specific work.

`search({ timeoutMs: 3000, serviceId? })` is lazy until awaited or connected. Repeated awaits share the scan; results deduplicate service identity. Connecting during a scan resolves before the full window ends as soon as a candidate passes verification. Connecting after an awaited scan rechecks its saved candidates without another broadcast. Empty collection returns `[]`; an empty connection attempt throws `SERVICE_NOT_FOUND`. Failed candidates produce `CONNECTION_FAILED` with per-candidate reasons. Newer explicit selections cannot be overwritten by an older search finishing late.

The native adapter sends one IPv4 discovery round on loopback and local broadcast destinations, accepts at most 64 endpoints, and ignores advertised address arrays in favor of the actual datagram sender. The SDK bounds verification to four concurrent requests and the requested time window. It checks a fresh public HTTP health nonce, service identity and API version before sending the configured token in a connection handshake. This is application-level identity verification, not TLS/server authentication.

After heartbeat loss, native recovery searches only for the selected service ID, with jittered backoff capped at 30 seconds. A verified new address updates the existing session, including already prepared tickets. Recovery performs no render or print submission and never generates a replacement intent. Ordinary browser imports have no UDP transport: `search()` rejects `DISCOVERY_UNAVAILABLE`; manual configuration still works. A trusted browser/native helper can supply the same `discover` callback contract through `createEntreePrint` dependencies.
