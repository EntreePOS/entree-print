# Entree Print SDK — 0.0.1-beta development

This is the SDK for the fresh 0.0.1 API under `/api`. There is no prototype API compatibility layer. Bundle the SDK directory with your application; `entree-print.mjs` imports `outbox.mjs`. It contains no installation token and does not download executable code from the print service. Entree Print is standalone; Entree POS is one possible client. The package is private while release verification remains incomplete.

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

## Implemented operations

- `config()`, `connect(options?)`, `disconnect()`, `target(name)`, awaitable `search()` and `search().connect()` with a native discovery transport. `connect({ ip, port, token })` accepts settings directly; `connect(server)` also accepts a record returned by `search()` and verifies its service ID. Omitted settings retain configured defaults. Existing targets keep their original service.
- Immutable `setContent()` and `setWidth()` builders, `render()` and `print()`.
- `target.status()`, `beep()`, `openDrawer()`, `cut()`, `sendCommand(Uint8Array)`; optional `idempotencyKey`/`metadata` on each command.
- `getPrinters({ refresh })`, `getJob(id)`, `getJob({ idempotencyKey })`, `getJobRender(id)`.
- `getJobs(printerName?, { station?, orderID?, status?, since?, limit?, cursor? })` returns `{ items, nextCursor }`; default 30, maximum 100. `status` accepts one state or an array. `since` is an ISO date/time with a time-zone offset or `Z`.
- `reprintJob(id, { idempotencyKey })` requires a new, persisted operator-action key and returns the linked receipt job.
- `subscribe(handler, { events: ['connection'] })`, returning `{ close() }`. Only local connection events are supported so far; job/printer event subscriptions are pending.
- Automatic service heartbeat after connect; `resume()` performs an immediate check. Browser pageshow/focus/visibility hooks trigger a fresh check and inventory refresh; Electron can supply its powerMonitor resume event through the native factory below.

Durable event replay and `after` actions are not implemented yet. Do not treat this as the finished API contract; see [API_DESIGN.md](../API_DESIGN.md).

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

The returned library is bound to the selected server. `connection.nodeSelection` contains `selectedServiceId`, `usedBackup`, and candidate states (`selected`, `failed`, `not_checked`) from that connection attempt; it is not a live health report for unused nodes. Retrieve installed queues with `getPrinters()`; there is no `printers` configuration mapping. Configure each eligible Windows queue directly to the intended Ethernet printer. The SDK checks that an automatically selected backup queue identifies a network host, but does not prove it reaches the same physical printer as the primary. USB or unidentified queues return `PRINTER_OWNER_REQUIRED`. Connect directly to the device owner for drawer, beep, cut or raw commands (`DEVICE_OWNER_REQUIRED`) and to the recorded job owner for reprints (`JOB_OWNER_REQUIRED`). These restrictions also cover the durable outbox.

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

An `ACK_INVALID` or transport failure after submission reports `delivery: 'unknown'`; its `details` includes `serviceId` and `idempotencyKey`. Retain and retry that intent. A later preview cannot replace the uncertain request's bytes. A server `RENDER_EXPIRED` rejection with `delivery: 'not_sent'` allows explicit `render()` and review before submitting the fresh preview with the same key. `print()` itself never silently refreshes a reviewed preview.

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

Local states are `pending`, `prepared`, `submitting`, `uncertain`, `needs_attention`, `accepted`, `cancelled` and `stopped`. A retryable preparation failure remains pending. A possible submission retains its bytes and uncertainty; the next flush reuses them. Hard errors such as an expired saved render require attention and block later items for that destination. `outbox.retry({ serviceId, idempotencyKey })` explicitly enables another attempt with the same bytes; it does not regenerate an expired render. `outbox.cancel({ serviceId, idempotencyKey })` is allowed only before a request has been saved for submission. It cannot cancel a possible Windows/plugin job. Accepted/cancelled keys remain retained, so another intended copy needs its own persisted action key.

If an operator handles an unresolved ticket separately, `outbox.stopRetrying({ serviceId, idempotencyKey })` marks that local intent `stopped` and lets later tickets proceed. It retains the saved request and uncertainty; it neither cancels an in-flight service/Windows job nor proves that it did not print. Show that distinction before offering this action. A stopped intent is not revived by enqueueing its key again; an explicit `retry()` restores the same request. Do not automatically stop unresolved tickets to clear a queue.

The outbox handles receipt jobs only. It does not automatically replay drawer, beep or raw commands. It does not run after the application closes, subscribe to heartbeat or transfer ownership to another server. Call `flush()` after reconnect/startup or on your application's retry action, and display unresolved records. A flush is not an atomic batch: another printer can accept its work even if one destination's storage fails.

Storage is scoped to this application origin and capped at 10,000 local records; full storage returns `OUTBOX_FULL` without evicting old keys. Records currently retain content, wire and acceptance evidence; automatic local pruning/export remains unfinished. Storage commits request strict IndexedDB durability and wait for transaction completion. Older engines may ignore that hint; no universal power-loss guarantee is claimed. Clearing site data removes this local history. Locks coordinate tabs on the same origin, not different tablets. See [IndexedDB transaction durability](https://developer.mozilla.org/en-US/docs/Web/API/IDBDatabase/transaction) and [Web Locks](https://developer.mozilla.org/en-US/docs/Web/API/Web_Locks_API).

Node/Electron main has no built-in IndexedDB. Applications there may pass a transactional `outboxStorage` dependency to `createEntreePrint`; there is no production file adapter yet. Its required methods are `list`, `add`, `update(previous, next)` and `withQueue(serviceId, printer, work)`. Inserts must atomically deduplicate `(serviceId, idempotencyKey)`, updates must compare versions, and queue locks must span all dispatchers sharing that storage. Do not substitute an in-memory map or unlocked JSON writes for durable storage.

Targets retain the service identity selected when created. Reconfiguring the default cannot send an existing ticket to another service. A different service occupying that address is rejected; verified recovery may update its endpoint for the same identity. Within one SDK client, identical configurations reuse a session and concurrent handshake. Verified aliases of the same service share one heartbeat/recovery monitor when their token, request deadline and heartbeat policy match. Different credentials or monitoring policies remain separate. Each caller gets its own connection-result copy.

Each monitor uses a fresh heartbeat request ID, never overlaps its polls, reports `degraded` then `offline` after three misses, and rechecks identity/inventory before returning `online`. Connection events include `printersStale`; they never manufacture a printer-offline or paper-out observation. Defaults are 5-second intervals and 2-second deadlines, configurable with `heartbeat: { intervalMs, timeoutMs, missedLimit }`. A verified address change updates every member while preserving each ticket's render, bytes and key. Older delayed handshakes cannot replace a newer address. Disconnect aborts sessions and clears monitor/timer/lifecycle registrations. Sharing does not extend across separate `createEntreePrint()` instances or POS processes and is not cross-server failover.

`disconnect()` aborts SDK requests and stops timers across its sessions. It does not cancel accepted jobs. Call `connect()` explicitly before further operations. Closing a subscription removes only its callback.

## Runtime and tests

Use a modern browser/Electron/Node runtime with Fetch, TextEncoder, AbortController and Web Crypto. Browser Web Crypto normally requires a secure context, and browser mixed-content/private-network rules still apply. In Node/Electron, use `createEntreePrint(options, { crypto: webcrypto })` with `webcrypto` imported from `node:crypto` when a global crypto implementation is unavailable. No fallback random IDs or weaker checksums are used.

`createEntreePrint()` creates an independent client for applications that need multiple separate configurations. Its optional second argument also accepts `fetch`, `now`, `setTimeout`, `clearTimeout`, `discover` and `subscribeResume` for controlled tests/native integration. `subscribeResume(handler)` must return a cleanup function. The discovery callback receives `{ signal, timeoutMs, serviceId?, onCandidate }`, reports `{ ip, port, serviceId?, protocol?, name? }` candidates, and returns a promise for transport completion. Cancellation must close native resources.

```powershell
node --test sdk/test/*.test.mjs
```

65 SDK tests pass, including controlled transport/discovery/heartbeat checks, durable outbox recovery cases and a real Chromium IndexedDB/Web Locks test. They cover direct/discovered-object connection, concurrent/lazy discovery, first-success connection, failures, cancellation, candidate floods, same-service address recovery, resume refresh, history filters and exact-byte retry. Six SDK/TestServer scenarios also run in the .NET suite. Real LAN discovery, OS sleep/resume and full installed-service network/physical-printer integration remain pending. The browser storage test requires Node.js 22+ and a local Edge or Chrome installation.

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
