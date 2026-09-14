# ENTREE Print API design

Status: target contract for the first 0.0.1 release, reviewed against the current Entree POS source on 2026-09-13. The user confirmed the plugin has not been adopted and requires no backward compatibility. There is one API under `/api`, API version `0.0.1`, and one random `AccessToken`. Do not preserve prototype endpoints, derived tokens or downloaded SDK execution. The D comparison output was accepted by the user. Production template coverage and reliability still need implementation and testing.

Latest correction: Windows printer drivers own paper, printable area, resolution and driver-supported cutter behavior. Do not ask customers to duplicate them in plugin profiles. The newly added profile editor was removed. Preview preparation reads the driver's current printable area/DPI, and printing preserves its page settings. `setWidth()` is optional content narrowing within that area, not paper configuration. Receipt pagination and driver-specific feeding/cutting still require physical validation. References below to printer settings mean measured driver settings; optional device-command configuration is separate and not required for ordinary receipt printing.

Working-tree update: the core HTTP contract and fluent SDK are implemented and tested, including real Chromium preview preparation, paged filtered history, explicit linked reprints and controlled SDK transport/heartbeat tests. See [implementation status](API_V2_IMPLEMENTATION.md) and [SDK usage](sdk/README.md) for exact scope. Job/printer events and ordered receipt actions are implemented; live Windows/network recovery remains under verification. The installed service remains unchanged; this document includes requirements not yet implemented.

The [service README](EntreePrintPlugin/README.md) documents the current API. The [comparison demo](samples/PrintComparison/README.md) documents the rendering experiments. Client-specific source reviews are maintained separately from this standalone product contract.

Confirmed scope: HTML text/layout, QR codes and barcodes. General image printing is excluded: no uploaded images, image URLs, logos, canvas screenshots or arbitrary image/SVG input. Service-generated vector QR/barcode shapes are supported content, not an image-upload feature.

### Delivery decision: Windows Print Spooler

Use installed Windows printer queues for all 0.0.1 receipt and device-command output. The deployment path is POS/tablet → Entree Print service → Windows Print Spooler and installed driver/port → USB, Ethernet or Wi-Fi printer. POS targets the Windows queue name; Windows configuration owns the printer port/address. Direct USB or TCP receipt delivery is outside this release scope. Read-only model-specific status queries can supplement Windows observations without becoming an alternative output path.

For receipts, preserve the accepted D approach: measure supported HTML, retain positioned Unicode text and vector rules/QR/barcodes, and draw through the Windows graphics/driver path. The driver produces device output at its supported resolution. Spooler use does not require making a receipt screenshot. Validate installed fonts, printable width (72 mm on the tested setup), margins and the driver-selected paper; driver compatibility does not guarantee arbitrary HTML coverage or identical results on every printer.

For `sendCommand()`, `beep()`, `openDrawer()` and supported explicit cuts, submit model-compatible RAW jobs through that same Windows queue. RAW still uses the spooler but bypasses normal document rendering; Windows does not translate ESC/POS into another printer language. Enable these capabilities only for verified queue/driver/model combinations. Prefer driver-managed cutting where configured. See [Microsoft WritePrinter](https://learn.microsoft.com/en-us/windows/win32/printdocs/writeprinter).

Entree Print owns validation, durable acceptance, idempotency, layout and tracking. Windows owns a job after successful handoff and manages its downstream queue/port processing. Retain the plugin ledger to reconcile lost acknowledgements and restarts; spooler adoption alone does not replace it. Monitor the exact Windows job instead of sending another copy when its printer is offline. Require queue visibility and print/monitor permissions for the Windows service account, not just the interactive user's account.

## 1. The everyday API

Keep the user's chain: configure the plugin, target a printer, set HTML, render for POS, and print. POS supplies the receipt; the plugin does not need an order schema.

```javascript
const print = await EntreePrint.config({ ip: "127.0.0.1", port: 9779, token: settings.printServiceToken }).connect();
const printers = await print.getPrinters();
const printer = print.target("cashier");
const ticket = printer.setContent(receiptHtml); // Width and DPI come from the Windows driver.

// Optional: display the prepared ticket in the POS interface.
const rendered = await ticket.render();
previewFrame.srcdoc = rendered.html;

// On the operator's print action. Persist this key with the POS print intent.
const job = await ticket.print({
  idempotencyKey: printIntent.id,
  metadata: {
    station: "POS-1",
    orderID: order._id,
    ticketNumber: order.number,
    source: "ticket",
    itemUniques: selectedItemUniques
  }
});
```

`render()` never prints, beeps, cuts, or opens a drawer. `print()` prepares automatically if necessary and returns once the exact print payload has been durably accepted by the plugin. It does not wait for paper output or the POS server's audit acknowledgement. Rendering may take time before acceptance; separate `render()` allows POS to do that work ahead of the print action.

## 2. Ownership

| POS owns | Plugin owns |
| --- | --- |
| Choosing cashier/kitchen/bar destinations and applying station preferences | Resolving a destination to an installed Windows printer |
| Item routing, separate slips, changed/voided items, languages and prices | HTML layout, font checks, preview artifact, printable width validation |
| Course timers, order state, payment flow, user permissions | Durable print jobs, printer serialization, device commands |
| Formatting receipt/report HTML and selecting barcode content | Supported rendering primitives and printer capability checks |
| Retaining a print intent until local acceptance; synchronizing business audit records | Delivery attempts, status observations, safe recovery after restart |
| Whether an operator requests a reprint | Recording the reprint's identity and original-job link |

Keep business methods such as `creditReceipt()` and `salesReport()` in the existing POS `Printer` facade and migrate its low-level submission path. `target("kitchen")` in this SDK means an actual printer name; POS expands its routing category `KITCHEN` into individual destinations before calling the SDK.

## 3. Method reference

Configuration/builders are synchronous. I/O methods return promises. No method silently opens a preview window.

| Object | Method | Return / meaning |
| --- | --- | --- |
| `EntreePrint` | `config({ ip, port, token, requestTimeoutMs?, heartbeat? })` | SDK object; stores settings, performs no connection attempt |
| `EntreePrint` | `connect(options?)` | `Promise<PrintLibrary>`; accepts connection settings or a discovered server object; retrieve printers with the returned library's `getPrinters()` |
| `EntreePrint` | `search({ timeoutMs?, serviceId? } = {})` | Awaitable discovery builder; awaiting returns discovered services, `.connect()` connects to the first verified available service |
| `EntreePrint` | `disconnect()` | Stops SDK heartbeat, discovery/reconnect work and subscriptions; does not cancel accepted print jobs |
| `EntreePrint` | `getPrinters({ refresh? } = {})` | `Promise<PrinterInfo[]>` |
| `EntreePrint` | `getJobs(printerName?, filters = {})` | `Promise<{ items: Job[], nextCursor: string \| null }>` |
| `EntreePrint` / connected library | `getJob(idOrJob)` or `getJob({ idempotencyKey, serviceId? })` | `Promise<Job>`; retrieve an accepted job by server ID or the POS's persisted intent key |
| `EntreePrint` | `getJobRender(idOrJob)` | `Promise<RenderedTicket>` for that job's retained artifact |
| `EntreePrint` | `reprintJob(idOrJob, { idempotencyKey })` | `Promise<Job>`; new job on the same destination, linked by `reprintOf` |
| `EntreePrint` | `subscribe(handler, { events? } = {})` | Subscription with `close()`; connection, printer and job changes |
| `EntreePrint` | `target(printerName)` | Independent `PrinterTarget`, no I/O |
| Printer target | `status({ refresh? } = {})` | `Promise<PrinterStatus>` |
| Printer target | `setContent(htmlOrBlocks)` | New `Ticket` builder with independent HTML or ordered content blocks |
| Printer target | `beep(options = {})` | `Promise<Job>`; profiled buzzer command |
| Printer target | `openDrawer(options = {})` | `Promise<Job>`; profiled cash drawer command |
| Printer target | `cut(options = {})` | `Promise<Job>`; standalone supported cutter command |
| Printer target | `sendCommand(bytes, options = {})` | `Promise<Job>`; explicit raw bytes |
| Ticket | `setContent(htmlOrBlocks)` | New ticket with all content replaced and no prepared cache |
| Ticket | `setWidth(mm)` | New ticket with printable content width in millimeters |
| Ticket | `render()` | `Promise<RenderedTicket>`; prepares or returns its cached artifact |
| Ticket | `print({ idempotencyKey?, metadata?, after? } = {})` | `Promise<Job>`; durable acceptance, not physical completion |

Corrections to the initial sketch: use `EntreePrint` consistently, numeric port, and `sendCommand()` rather than `sentCommand()`. There is no separate `preview()` method.

### Configuration and destination identity

`ip` is the computer running the plugin, not the Ethernet/Wi-Fi printer's own address. Existing POS supports either a local print service or a designated host; retain that selection. A USB printer connected to that host remains usable through the same API.

`config()` and `connect(options)` apply to new targets. In single-server mode a target captures its selected service identity, initial endpoint and printer name, so later configuration cannot redirect an already prepared ticket to another service. Verified address recovery may update the endpoint for that same service identity. The planned node mode captures the configured destination pool instead, binding each submission to one owning service. Job/render results include a persistent `serviceId`; their IDs are scoped to that service. A service address pointing at a different installation must not resolve an old ID as a new print request.

`config(...).connect()` performs a bounded service handshake and inventory validation, then returns a connected print library. Retrieve printer objects through `await print.getPrinters()`. The library captures its service; changing the top-level configuration cannot redirect its queries or newly created tickets to another service. `config()` alone stores settings; it does not connect. Other operations may connect lazily. Connection failure throws an `EntreePrintError`; there is no infinite readiness loop or false success object. Bundle the versioned library with the POS and configure allowed origins and authorization for LAN writes. The handshake returns data; the SDK returns the callable library object.

```javascript
const print = await EntreePrint.config({ ip: "127.0.0.1", port: 9779, token: settings.printServiceToken }).connect();
// Or pass the settings directly:
const direct = await EntreePrint.connect({ ip: "127.0.0.1", port: 9779 });
// Or connect to the first verified available print service:
const discovered = await EntreePrint.search().connect();

// Or collect all discovered services for POS to display/select:
const servers = await EntreePrint.search();
// [{ serviceId, ip, port, name }, ...]; [] when the search window finds none
if (servers.length) await EntreePrint.connect(servers[0]);

// All connect variants return the same print-library interface:
const printers = await print.getPrinters();
// [{ name: "cashier", isDefault: true, connection: {...}, settings: { source: "windows_driver" }, status: {...} }]
const server = print.connection; // Server details only; no printers or credential.
```

The returned library exposes `target`, `getPrinters`, `getJobs`, `getJob`, `getJobRender` and `reprintJob`. `print.connection` returns a copy of server metadata including `serviceId`, `bootId`, `ip`, `port`, versions and capabilities. Connection management (`config`, `search`, `disconnect`, `resume`, `subscribe`) stays on `EntreePrint`; disconnect stops all its sessions. Installed printers can be offline, unknown or virtual; discovery must not hide them or call them ready. An unavailable service throws `SERVICE_UNAVAILABLE`; a reachable service with no installed printers throws `NO_PRINTERS` with its service identity in `details`. A failed printer query throws `PRINTER_QUERY_FAILED`, distinct from a successful empty list. `getPrinters({ refresh: true })` refreshes the Windows query and can return an empty array when printers are removed later.

### Automatic discovery and reconnect

`search()` returns a synchronous, awaitable discovery builder so both `await EntreePrint.search()` and `await EntreePrint.search().connect()` work. Awaiting the builder collects verified service records; `.connect()` discovers and connects. Calling `search()` alone does not send packets. Reusing/awaiting one builder shares its bounded discovery session rather than starting duplicate scans. A successful discovery connection sets the default service for future top-level `target()` calls; existing targets/tickets retain their captured service. Overlapping connection attempts must not let a late, older completion overwrite a newer connection selection.

Confirmed first-found behavior: `await search()` collects all verified services found within a proposed 3-second discovery window, returning an array (empty when none are found). One service record represents a verified reachable endpoint; multiple advertised IPs for the same service are deduplicated by service ID with the selected reachable `ip`/`port`. `search().connect()` connects to the first candidate that passes compatibility, authorization and the connection/inventory handshake, without waiting for the whole list. “First” depends on response timing and is not a stable priority. Other candidates can be tried within the overall bounded deadline if a candidate fails. If none is discovered, throw `SERVICE_NOT_FOUND`; if candidates are found but none connects, throw `CONNECTION_FAILED` with per-candidate reasons. This is distinct from a successful empty search array. No additional ambiguity-selection requirement applies to initial first-found connection. POS can still choose explicitly from the full search results, or constrain discovery with `serviceId`.

The existing plugin has UDP discovery on 9778. Entree POS can implement this through its Electron/Node layer; it already uses native network discovery for the POS server. Keep print-service discovery distinct from discovery of the POS business server. Validate response service/protocol/version and advertised HTTP endpoint, then perform an authorized handshake; a broadcast reply alone is not trusted service identity. Deduplicate multiple addresses for the same service. Add persistent service IDs/version capability data to the current discovery response.

Ordinary browser code cannot use Node's UDP discovery interface. Provide discovery through the Electron/native adapter, a reachable local helper, or a configured directory of allowed service candidates. When no discovery transport is available, reject with `DISCOVERY_UNAVAILABLE` and allow manual `config()`; do not imply a browser can broadcast UDP with `fetch()`. No subnet-wide HTTP scanning or silent cross-store selection. See [Node's UDP API](https://nodejs.org/api/dgram.html) for the native implementation boundary.

After a Wi-Fi reconnect or address change, recover the selected service ID for its existing jobs. The planned failover mode below permits new work to move to configured alternatives. Never move unresolved jobs to a different host automatically: their deduplication ledger belongs to the original service. Connection failures do not create new print-intent IDs. See the [release checklist](BETA_RELEASE_CHECKLIST.md) for recovery evidence.

### Configured nodes and printing failover

Latest requirement: `connect()` accepts an object, and another available print node can continue printing when the preferred node fails. Direct-object, discovered-object and ordered initial node selection are implemented. This example selects a server during connection; automatic routing during printing and transfer of existing jobs remain proposed work.

```javascript
const print = await EntreePrint.connect({
  nodes: [
    {
      serviceId: settings.primaryServiceId,
      ip: '192.168.1.10', port: 9779, token: settings.primaryToken
    },
    {
      serviceId: settings.backupServiceId,
      ip: '192.168.1.11', port: 9779, token: settings.backupToken
    }
  ],
  failover: true,
  timeoutMs: 10000
});

const printers = await print.getPrinters();
await print.target('Kitchen Printer').setContent(receiptHtml).print({
  idempotencyKey: printIntent.id
});
```

Confirmed normal topology: the cashier receipt printer uses USB; regular printers use Ethernet. Configure each eligible print-server node with its own Windows queue pointing directly to the same physical Ethernet printer. A queue shared through the failed primary computer is not an independent backup. Use installed Windows queue names; there is no plugin `printers` mapping or duplicate driver profile. Queue names alone do not prove that both queues reach the same device. Current inventory exposes a comparable `connection.destination.id` for direct standard TCP/IP queues, including IP, protocol, port and LPR queue. Selected-backup submission refreshes inventory and rejects changed, missing, ambiguous or stale destinations. Matching IDs establish endpoint configuration equality; physical identity and layout equivalence still require separate verification. Keep the cashier USB destination pinned to its connected host; host failure reports it unavailable. Moving cashier receipts to another physical printer requires an explicit destination choice and is not the default behavior.

Nodes are ordered by preference and each has its own credential and verified identity. Discovery provides candidate addresses, not permission to route to an arbitrary store or printer. Printer failure is distinct from print-server failure: a second server does not resolve paper-out, a jam or a disconnected Ethernet printer when it targets that same device.

The following table describes the full intended routing behavior. Currently, only initial connection selection is automatic; an existing ticket never changes its owner.

| Job situation | Failover behavior |
| --- | --- |
| Initial connection fails, or a new intent has never had a submission attempted | Choose the next authorized node with a configured destination, verify its queue/profile and prepare there |
| Every submission attempt has authoritative non-acceptance evidence | Keep the same POS intent key; it may move to an eligible backup |
| Any submission may have reached the original node, including timeout or lost ACK | Keep the original owner and report uncertainty; retry/reconcile there with the same bytes/key |
| Original node durably accepted the job, including a job waiting for a printer | Original node owns delivery; no second submission to a backup |
| Operator explicitly requests another copy | A separate, persisted action after showing any uncertainty; never an automatic retry |

The latest error alone is insufficient to authorize failover: the entire intent must have no unresolved prior attempt. Persist owner selection and attempt state in the POS outbox before sending bytes. A second POS station must not independently fail over the same intent; cross-client ownership requires a shared atomic claim. Current per-node idempotency files cannot deduplicate work accepted by two different nodes. A shared job registry alone would also be insufficient to transfer accepted work: ownership fencing and Windows handoff reconciliation are required to stop the old owner from printing after recovery. Even with coordination, uncertain physical output must remain uncertain.

Failover does not reuse a render ID on a different service. Select the destination before preparation where possible. For an already reviewed preview, retain that exact layout only if the backup can validate and produce it equivalently; otherwise require a fresh preview and review. The service can now export optional portable drawing data and validate it through `POST /api/renders` with `{ printer, artifact }`, preserving geometry and expiry under a new local render ID. This preparation does not claim or transfer job ownership. SDK automatic use and coordinated routing remain implementation work; see API_V2_IMPLEMENTATION.md. Do not silently change width, font or barcode geometry. Standalone drawer, beep, cut and raw commands stay bound to their configured physical device; receipt failover does not authorize moving those actions.

In node mode, connection results must distinguish the active node, each node's health and mapped destination availability. Job responses retain their actual `serviceId` and Windows queue; reads/reprints must resolve the recorded owner rather than whichever node is currently active. Emit a connection transition identifying the old/new node and reason. Heartbeats may select a backup for eligible new work; they do not dispatch existing jobs. Bound node selection time, retain pending intents when no node qualifies, and avoid automatically switching back while an intent is being prepared or submitted.

Required verification: primary unavailable before connect, failure before submission, lost ACK after acceptance, failure during Windows handoff, primary recovery, two stations racing the same intent, different queue names/profiles, unavailable USB host, no eligible backup, preview equivalence and device-command isolation. Node failover remains a release gate until these pass.

### Built-in heartbeat and address recovery

Heartbeat starts automatically after a successful `connect()`. No POS timer is required. Proposed configurable defaults:

```javascript
const data = await EntreePrint.config({
  ip: "127.0.0.1",
  port: 9779,
  heartbeat: { intervalMs: 5000, timeoutMs: 2000, missedLimit: 3 }
}).connect();

const subscription = EntreePrint.subscribe(event => {
  if (event.type === "connection") updateServiceIndicator(event.data);
  if (event.type === "printer") updatePrinterIndicator(event.data);
});

// On application teardown, not when merely closing the preview:
EntreePrint.disconnect();
```

Two separate monitoring paths are required:

| Path | Owner and observation | What it proves |
| --- | --- | --- |
| POS SDK → print service | Lightweight application heartbeat with a fresh request ID, persistent service ID and process boot ID | This service instance is responding; not that a printer has paper or that a ticket printed |
| Print service → each printer | Background Windows status plus model-supported, read-only device queries | Printer/transport observations, paper/cover state where supported; unavailable sensor data stays unknown |

The SDK uses at most one heartbeat in flight per connected service and never overlaps polls after a slow response. Each response must match the request ID and selected service ID; cache responses and late responses from a prior connection generation cannot reset the miss count. First missed deadline sets connection state `degraded`; three consecutive misses set `offline` (meaning service unreachable from this client). Record `lastSeenAt`, `missedCount`, and a reason. A valid current reply resets misses. Recovery stays `reconnecting` while the SDK verifies service/boot identity, refreshes printer snapshots and reconciles job/event cursors, then returns `online`. Emit connection events on meaningful transitions rather than flooding POS every interval.

The interval/miss threshold is a nominal detection policy, not an exact wall-clock guarantee: a suspended POS process cannot run its timer. On resume, perform an immediate check and mark cached observations stale until refreshed. Service heartbeats and printer observation timestamps are independent; the service can answer while its printer-status worker is delayed. Each printer result therefore retains its own `observedAt`, `source` and `stale`, rather than taking the heartbeat time as a new hardware reading. Deduplicate printer probes across POS clients and use printer-specific bounded timeouts; one silent printer must not block all others or service heartbeat responses. Never use a print, cut, drawer pulse or arbitrary raw bytes as a liveness probe.

On service heartbeat loss, printer observations become stale/unknown from this POS's perspective. Do not set every printer's physical state to offline just because the service disappeared. While the service remains reachable, a confirmed printer offline observation can update that printer alone. A read timeout indicates unreachable/unknown unless the reporting adapter supplies stronger offline evidence. Paper-out is an attention state distinct from network reachability. Where no supported status channel exists, report that limitation; heartbeat cannot manufacture USB or paper-sensor support.

For an IP change (for example, DHCP lease reassignment) or an unresponsive configured address, the SDK reconnects in the background using capped backoff with jitter and native discovery constrained to the saved service ID. A proposed schedule starts near 1 second and caps at 30 seconds between attempts; only one recovery loop runs per service. Initial `search().connect()` uses first-found selection; address recovery preserves identity. Separately, configured node failover may choose another identity for eligible new work under the rules above. If a different host now occupies the old IP, reject it as `SERVICE_MISMATCH`. Existing single-server targets/tickets follow an updated endpoint only after the same service identity is verified. Without a discovery adapter, keep that service offline and allow manual address correction followed by identity verification.

An actual printer's IP change is separate: the SDK cannot repair a Windows printer port by rediscovering the plugin. The service can flag an unreachable destination or verified device-address mismatch; update a printer profile/port only through an explicit verified configuration workflow. Do not reroute by similar printer name or an arbitrary responding IP. A genuinely static address does not have a DHCP lease to renew, and heartbeat diagnoses lack of response rather than proving its cause.

Heartbeat/reconnect never cancels, reprints, or assigns a new key to accepted/uncertain jobs. The plugin keeps its durable queue independently of client connection state. New work without confirmed acceptance remains in the POS outbox. `disconnect()` cancels SDK monitoring/recovery and marks it explicitly disconnected; it does not stop the plugin or its queue. Operations on an explicitly disconnected SDK require a new `connect()` and otherwise reject with `NOT_CONNECTED`. Closing one `subscribe()` handle removes that listener; it does not stop shared heartbeat monitoring.

Before Windows handoff, a current offline/paper-out/cover-open/paused observation leaves the accepted receipt in `waiting_for_printer`. Waiting is not a delivery attempt and does not expire merely because the backend retry count is reached. Its content remains durable across plugin restart, while other printers continue independently. Once the condition clears, the same job continues in its original printer queue. After handoff, Windows owns its queued job; plugin recovery must not create a second copy. Unknown delivery remains `needs_attention`.

Connection events are local SDK observations with local sequence/version identity, not replayed server facts. Printer/job events retain the durable service event identity described below. `lastSeenAt` is when this SDK last received a valid response, separate from any server timestamp; clock differences must not be used to infer failure.

Each `PrinterInfo` contains `{ name, isDefault, connection, settings, status }`. `connection.type` is `usb`, `network`, `other`, or `unknown`, with an optional observed host address for network printers. `settings.source` is `windows_driver`; actual printable width and DPI are read on preparation, without forcing slow driver queries into heartbeat/inventory responses. `status` follows the status contract below and includes capabilities. A printer being listed does not by itself mean it is online.

### Independent tickets

```javascript
const printer = EntreePrint.target("cashier");
const first = printer.setContent(firstHtml).setWidth(72);
const second = printer.setContent(secondHtml).setWidth(72);

await first.render();
const revised = first.setContent(correctedHtml); // first is unchanged
```

Builder methods return new ticket objects. Content is never stored as mutable state on the printer target. Block arrays/options are copied on input so later POS mutations cannot alter a snapshot. Each snapshot owns its rendering promise/cache; concurrent `render()` calls share preparation. Repeated `print()` calls on the same unchanged snapshot use its generated idempotency key unless the caller supplied one. They return the same job rather than creating extra copies. Changing a snapshot generates a new default key. POS must explicitly retain its business key across reloads; generated in-memory keys cannot do that.

Copies are separate print intents initially. This matches separate kitchen slips, per-item labels and reprints, and avoids an opaque multi-copy job whose partial completion cannot be identified.

## 4. Render result and HTML preview

Illustrative response values:

```javascript
{
  id: "render-uuid",
  serviceId: "service-uuid",
  html: "<!doctype html><html>...<svg>...</svg></html>",
  widthMm: 72,
  heightMm: 215.4,
  renderer: "positioned-text",
  rendererVersion: "0.0.1-beta",
  profileVersion: "profile-revision",
  expiresAt: "2026-09-13T20:30:00Z",
  warnings: []
}
```

The service returns a display-ready, script-free HTML document wrapping the prepared SVG text/rules. It does not return the original HTML and ask the browser to calculate the receipt layout again. POS can put it in a sandboxed iframe with `srcdoc`; HTML must not require a network image URL or a live service connection after it is returned. Font availability can still affect browser appearance and must be surfaced in compatibility checks. Do not promise identical glyph rasterization across all client machines.

The current retail history view assigns synchronous HTML into a shadow root. Migrate it to an awaited render and an iframe; use a request counter so a slow result for the previous order cannot overwrite the current preview. Keep template generation local to POS, but label any local source-layout fallback as an approximation rather than a prepared print preview.

The plugin stores the prepared display list with resolved fonts, dimensions, renderer version and content hash. `print()` uses that artifact. Rendering does not reserve a place in a printer queue. If a prepared artifact expires before acceptance, return `RENDER_EXPIRED`; do not silently recreate a different layout after an operator has reviewed it. The caller can render again and review the new result. If `print()` has never been preceded by render, preparation happens automatically.

`getJobRender(id)` returns the accepted job's retained rendering, not a fresh layout built from the order's current contents. If its artifact has been purged, return `ARTIFACT_EXPIRED`. Never substitute a newly generated receipt for an historical preview.

Use the Windows driver's printable area, not an assumed 80 mm or 72 mm width. `setWidth()` optionally narrows content within that area; omitting it reads the driver width. Paper and DPI remain Windows settings. An unavailable driver measurement returns `PRINTER_SETTINGS_UNAVAILABLE`; overflow returns `LAYOUT_OVERFLOW`. Do not silently shrink a reviewed receipt.

### QR codes and barcodes

Keep the same `setContent()` API. A string means HTML; an array describes ordered HTML and code blocks. This proposed extension follows the existing plugin's content-block concept without introducing a separate builder method for every code format.

```javascript
const ticket = EntreePrint.target("cashier")
  .setWidth(72)
  .setContent([
    { type: "html", html: receiptHtml },
    { type: "qrcode", value: order._id, sizeMm: 24, align: "center" },
    {
      type: "barcode", format: "code128", value: "ORDER10086",
      heightMm: 12, showText: true, align: "center"
    },
    { type: "html", html: "<p>Thank you · 谢谢</p>" }
  ]);

const rendered = await ticket.render();
previewFrame.srcdoc = rendered.html;
await ticket.print({ idempotencyKey: printIntent.id });
```

Initial formats: QR Code, Code 39 (used by existing POS receipts), and Code 128 (already present in the main plugin's block renderer). Other symbologies return `BARCODE_FORMAT_UNSUPPORTED` until explicitly implemented.

| Block | Fields |
| --- | --- |
| HTML | `{ type: "html", html: string }` |
| QR | `{ type: "qrcode", value: string, sizeMm?: number, align?: "left" \| "center" \| "right" }` |
| Barcode | `{ type: "barcode", format: "code39" \| "code128", value: string, heightMm?: number, showText?: boolean, align?: "left" \| "center" \| "right" }` |

Proposed defaults: QR target size 24 mm including the quiet zone; barcode bar height 12 mm with readable text below; center alignment for both. QR uses UTF-8 with the encoder's explicit encoding metadata; non-ASCII QR payloads need real scanner fixtures. One-dimensional values must satisfy their format's character/length rules; never silently uppercase, trim or replace data. Empty/invalid values fail with `BARCODE_VALUE_INVALID`. Only the documented block types are accepted.

The service generates encoded modules/bars once and stores their vector geometry in the same display list as receipt text. `rendered.html` contains inline SVG shapes for both codes, and Windows printing draws those same shapes. POS sends the value, not a PNG or third-party SVG. The encoder manages required quiet zones, checksums and QR error correction; the initial QR error-correction policy is fixed and versioned rather than exposed as another everyday option.

Use whole printer dots for code modules and bar edges according to the Windows driver's DPI. QR `sizeMm` is a target: select a valid whole-dot module size within it, including the quiet zone; report the resulting bounds in optional `rendered.codes` entries (`blockIndex`, `type`, `xMm`, `yMm`, `widthMm`, `heightMm`, `moduleDots`). Barcode width follows encoded content and module size; do not stretch it to fill the receipt. `heightMm` covers bars only; quiet zones and optional readable text contribute to total layout height. If a value cannot fit with the renderer’s tested minimum module size, return `CODE_TOO_DENSE` or `LAYOUT_OVERFLOW`, rather than reducing it to an unreliable width. The service reads driver DPI automatically; no customer DPI profile is required.

Current barcode rendering uses a minimum physical module width around 0.25 mm and a preferred width near 0.375 mm, rounded to whole dots. Generated codes and their labels/quiet zones stay together during page splitting. Long receipts use the Windows driver's printable page height without shrinking; HTML `break-inside: avoid` also protects sections. An indivisible section taller than a page returns `LAYOUT_ITEM_TOO_TALL` before Windows submission. A multi-page receipt remains one Windows spooler job. Physical driver feed/cut behavior still needs validation; the current POS preview is continuous HTML.

Blocks flow top-to-bottom in one document, with each code reserving its complete bounds so following content cannot overlap its quiet zone. HTML blocks in an array are fragments sharing one document/style context, not separate full HTML documents. No absolute code positioning is needed for the initial receipt API. Arbitrary in-table code placement and label/A4 page positioning remain separate layout work.

Existing POS `ADD_PRINT_BARCODE` calls become QR/barcode blocks in the intended receipt order. Label code that currently generates a QR PNG must pass its original QR value instead; do not add general image support to preserve that implementation detail. Label media/pagination still require their own migration work.

`render()` must show every requested code and readable barcode text before printing. Code payload, format and geometry participate in the artifact hash and idempotency comparison; changing any code invalidates the prepared layout. General image input returns `HTML_UNSUPPORTED` or `CONTENT_TYPE_UNSUPPORTED` before print acceptance, with a clear explanation. POS templates must remove images or replace code images with semantic QR/barcode blocks before using this plugin. There is no legacy image backend in the release API.

## 5. Jobs, metadata, and duplicate prevention

```javascript
const recent = await EntreePrint.getJobs("kitchen", {
  station: "POS-1",
  orderID: order._id,
  since: thirtyMinutesAgo,
  limit: 30
});

const current = await EntreePrint.getJob(job);
const oldPreview = await EntreePrint.getJobRender(job);

// Only for an intentional additional copy; persist a new action key.
const copy = await EntreePrint.reprintJob(job, {
  idempotencyKey: reprintAction.id
});
```

Filters: `station`, `orderID`, `status`, `since`, `limit`, `cursor`. `status` is an array of job states. Default limit 30, maximum 100, newest accepted first with an ID tie-breaker. An omitted printer name means all plugin-owned jobs on this host; pass `undefined` when supplying filters alone. Results do not include arbitrary Windows jobs belonging to other applications. `getPrinters()` and printer status may report an external queue count separately.

Job contract:

```javascript
{
  id: "job-uuid",
  serviceId: "service-uuid",
  idempotencyKey: "pos-persisted-print-intent-id",
  printer: "kitchen",
  type: "print", // or "beep", "open_cash_drawer", "cut", "send_command"
  state: "accepted",
  renderId: "render-uuid", // null for commands
  metadata: {
    station: "POS-1", operator: "cashier-name",
    orderID: "order-id", ticketNumber: "10086", ticketType: "DINE_IN",
    source: "ticket", title: "Kitchen ticket",
    itemUniques: ["line-id"], itemNames: ["Fried rice"]
  },
  reprintOf: null,
  integrity: { verified: true, algorithm: "sha256", requestDigest: "<64 lowercase hex characters>" },
  delivery: { state: "not_sent", evidence: "none", spoolerJobId: null },
  spooler: { state: "not_submitted", jobId: null, queue: null, observedAt: null, windowsStatus: [] },
  reason: null,
  version: 1,
  acceptedAt: "2026-09-13T20:00:00Z",
  updatedAt: "2026-09-13T20:00:00Z",
  artifactExpiresAt: null, // Set after a terminal completion, never while waiting.
  artifactExpiredAt: null
}
```

The service publishes its retention policy through the connection and expiry timestamps on jobs. Default receipt retention is seven days after completion, configurable from 1–365 days through `ReceiptRetentionDays`. Pending and delivery-uncertain jobs cannot be silently evicted. Finished layouts are compacted by a bounded background worker; job history, command identity, content hash and the original request digest remain. The exact original bytes remain replayable for the lifetime of the retained ledger, including after receipt expiry. A different request for a compacted key receives `IDEMPOTENCY_CONFLICT`; requesting its preview or a new reprint receives `ARTIFACT_EXPIRED`.

The ledger admits at most 100,000 intent records, 10,000 prepared receipts and 512 MB of serialized records before new acceptance is refused. Existing status writes can grow beyond that admission budget. Intent records have no automatic expiry: when capacity is reached, reject new work with `QUEUE_FULL` while preserving existing jobs and retries. This deliberately avoids forgetting old opaque keys and mistaking their delayed retries for new work. Automatic history export/removal is not implemented; a future bounded replay-expiration scheme must explicitly reject old intents before deleting their tombstones.

The SDK accepts returned jobs or `{ id, serviceId }` references for status, retained preview and explicit reprint, resolving a previously connected original owner independently of the selected server. Include `renderId` to verify the retained rendering identity. Key lookup also accepts `{ serviceId, idempotencyKey }`. Bare IDs stay library-bound. Unknown owners require an explicit connection; job data never supplies credentials or endpoints. Lookup failures remain uncertain and never authorize routing to a different node. These methods preserve ownership but do not implement automatic failover for new print submissions.

Metadata maps existing `getPrintMeta()` fields; keep `orderID` spelling for migration. It is searchable context, not instructions to interpret the order. Full HTML belongs in retained artifacts, not list results, event payloads or routine logs. Command jobs have no render and return `RENDER_NOT_AVAILABLE` for render lookup. `reprintJob()` accepts print jobs only; repeating a drawer/raw command requires a new explicit command action.

Reprinting an active job (`accepted`, `waiting_for_printer`, `submitting`, `submitted`, `printing`, `blocked`) returns `JOB_NOT_REPRINTABLE`; it could still produce paper. For a `needs_attention` job, POS must show the uncertainty before the operator explicitly requests another copy. Reprinting does not cancel a potentially remaining Windows job. Initial reprints preserve the original destination and output; selecting a different printer or adding a REPRINT heading is a new POS-composed ticket with a new intent.

### Key rules

1. POS creates and persists a unique intent for each destination, order revision, slip/item group and intended copy. Do not use only the order ID: different printers, later item changes and genuine reprints are distinct actions.
2. Network retries reuse that exact key and payload. The plugin commits the key, immutable payload/artifact and queue entry transactionally before returning `accepted`.
3. The same key and canonical payload return the same job. The same key with a different printer, artifact/content, command, metadata or post-print actions returns `IDEMPOTENCY_CONFLICT` (409). Render IDs alone are not content identity; compare the stored canonical payload/hash.
4. Look up an existing idempotency key before requiring a still-live preview artifact. A repeated accepted request must still return its job after the preview TTL expires.
5. An explicit reprint creates a new job and links the original. Do not mutate or erase the original job's delivery evidence.
6. Serialize writes per resolved physical Windows queue across all clients. Different printers can work independently. Physical exactly-once output cannot be guaranteed across a crash during handoff to hardware; uncertain handoff is a first-class outcome.

POS server audit `_id`, plugin job `id`, and Windows `spoolerJobId` are different identifiers. Audit synchronization keys on `(serviceId, job.id)` and upserts versions, so reconnects do not create duplicate audit rows. A cross-station reprint is sent once to the owning service with one persisted action key; it must not be broadcast for every station to execute.

### Request integrity and validation

Every render, print, reprint and device-command submission requires exact-byte SHA-256 validation, generated automatically by the SDK. POS programmers keep using `render()` / `print()` / helpers without constructing checksums. The old normalized-JSON checksum protocol and derived-token implementation are removed.

The SDK serializes the request once as UTF-8 JSON without a BOM, hashes those exact bytes and sends them unchanged with `X-Entree-Content-SHA256: <64 lowercase hex characters>`. The checksum is outside the body to avoid circular hashing. The API accepts uncompressed request bodies initially; the service hashes the bounded raw body before JSON deserialization or normalization. Configure CORS to allow this header for authorized POS origins. Chinese text, escaped characters, whitespace and number formatting therefore do not require matching JavaScript and .NET JSON serializers. A non-SDK caller must hash its actual transmitted bytes.

Before creating any job or printer side effect, require a complete body within the advertised size limit, a valid matching digest, valid UTF-8/JSON, supported schema/version/command, authorized destination, and valid fields and limits. Reject duplicate JSON property names, unsupported fields and invalid command bytes; a matching checksum alone does not make a command valid. Render preparation validates supported HTML/code content; job acceptance validates the referenced immutable artifact and its destination/profile compatibility. Authorization is separate: an unkeyed checksum detects byte changes but does not authenticate the sender. Require deployment authentication and protected transport; do not treat knowledge of a checksum algorithm as permission to print.

Reject missing/malformed/mismatched digests with `CHECKSUM_MISSING`, `CHECKSUM_FORMAT_INVALID` or `CHECKSUM_INVALID` before acceptance. An incomplete upload may end without a response; POS treats that as unacknowledged and retries its original intent. Check integrity and schema even for idempotent replay, then look up the existing key before requiring a live render artifact. An invalid attempt never changes or cancels a previously accepted job with that key.

The accepted Job stores `integrity: { verified: true, algorithm: "sha256", requestDigest }` for its original acceptance request, and acceptance/replay HTTP responses echo the verified current request digest in `X-Entree-Content-SHA256` (expose it through CORS). This response header identifies the validated request; it is not a checksum of the response. The SDK verifies the echoed digest before resolving submission. A missing/mismatched acknowledgement digest is `ACK_INVALID` with acceptance `unknown`: reconcile/retry with the same key, never create another print intent.

The transport checksum and idempotency content hash serve different purposes. The first checks transmitted bytes; the second compares the validated command's meaning and resolved artifact. Semantically identical JSON with different property order can have different request digests and still resolve to the same job. SDK retries retain their original bytes/key. A changed command with a recomputed checksum and reused key still returns `IDEMPOTENCY_CONFLICT`.

## 6. Truthful status and recovery

| Job state | Meaning / operator behavior |
| --- | --- |
| `accepted` | Payload and queue entry saved by the plugin; safe for POS to finish local handoff |
| `waiting_for_printer` | Not sent; paper out/offline/cover open or another known blocker; eligible for safe later delivery |
| `submitting` | Handoff underway; a crash here needs reconciliation, not blind replay |
| `submitted` | Windows spooler accepted the full document; physical completion not confirmed |
| `printing` | Positive job-specific progress observation, when available |
| `completed` | Tracked processing ended; inspect `delivery` to understand the evidence |
| `needs_attention` | Delivery may have occurred or partially occurred; operator review needed |
| `failed` | Known rejection or failure without a successful handoff; reason explains next action |

`delivery.state` is `not_sent`, `sent`, `confirmed`, or `unknown`. `delivery.evidence` is `none`, `windows_spooler`, `transport`, or `device`. Show **Printed** only for job-specific physical confirmation (`confirmed` with suitable device evidence). A successful Windows completion can be `completed` / `sent` / `windows_spooler`, displayed as **Completed by Windows**, not a paper-output guarantee. Merely observing an empty queue is insufficient: a job can have been cancelled or lost. A job disappearing without known completion becomes `needs_attention` / `unknown`.

`reason` includes a stable code and readable message. Waiting jobs keep their queue position; do not replay jobs already in the Windows queue after a paper refill. Timeout is evidence of a timeout, not proof of a failed or completed print. A transport failure after bytes may have been accepted becomes uncertain delivery. The plugin must capture Windows job identity and handoff progress; today's `WindowsTextPrinter.Print()` return value alone cannot implement this contract.

### Windows spooler acknowledgement

Expose three milestones independently: `integrity.verified` means the request passed validation; job `accepted` means the plugin saved responsibility for it; `spooler.state` reports Windows processing. `print()` continues to resolve at durable plugin acceptance, with later Windows outcomes available through `subscribe()` and `getJob(id)`. POS can display each milestone without blocking payment on a driver or Wi-Fi delay.

`spooler` contains `{ state, jobId, queue, observedAt, windowsStatus }`. States are `not_submitted`, `submitting`, `submitted`, `printing`, `blocked`, `completed`, `failed`, `cancelled` and `unknown`. Preserve raw Windows status flags and a readable job reason. All v2 output uses Windows queues. `spooler.jobId` and `delivery.spoolerJobId` refer to the same tracked Windows job. A blocked existing Windows job remains handed off; do not move it to the plugin's not-sent queue or submit another copy.

Capture the actual Windows job ID from the submission API and persist it with the service job ID, resolved queue, attempt ID, unique document correlation name and submission time. Do not match solely by receipt title, queue position or numeric job ID after a restart. GDI `StartDoc` returns the job identifier on success ([Microsoft StartDocW](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-startdocw)). The production driver now uses a native GDI bridge sharing D's drawing code, records a unique attempt name before submission, captures the Windows ID immediately, and monitors notifications plus `GetJob`. Single and concurrent virtual PDF jobs captured positive Windows completion in the working tree. The original D comparison remains available; physical quality, hardware fault pilots and the API response shape still require verification/implementation. Merely returning from `Print()` cannot fulfill this contract.

Register queue change monitoring before submission, then track the specific job using Windows notifications plus bounded `GetJob` reconciliation. Persist progress and observed terminal evidence before publishing events. Notifications can trigger a fresh status read; a missed notification or a job already deleted before that read does not imply success. Run blocking driver calls outside the request/UI path, and recover monitoring after spooler/service restarts. See [Microsoft printer change notifications](https://learn.microsoft.com/en-us/windows/win32/printdocs/findfirstprinterchangenotification).

Record the job ID while `submitting`; report `submitted` only after the full document handoff succeeds. Positive Windows completion evidence maps to `spooler.state: "completed"`, with the applicable Windows flags retained and delivery evidence `windows_spooler`. Windows `JOB_STATUS_COMPLETE` means sent to the printer; even `JOB_STATUS_PRINTED` can be reported early by monitors without TrueEndOfJob support. Therefore display **Completed by Windows**, reserving physical **Printed** for suitable device confirmation. See [Microsoft JOB_INFO_2](https://learn.microsoft.com/en-us/windows/win32/printdocs/job-info-2).

Paper-out, pause or offline flags produce a blocked reason and keep monitoring the same job. Deletion/cancellation is not completion and does not prove that no paper was produced. A failed handoff or restart after possible partial delivery, unexplained disappearance, or completion-observation timeout produces `needs_attention` with delivery `unknown` unless stronger evidence is available. Neither a healthy heartbeat nor an empty Windows queue upgrades that outcome to success. Reconcile uncertainty without automatic reprinting; track trailing cut/beep phases separately from receipt completion.

Printer status is separate from job status:

```javascript
{
  printer: "kitchen",
  state: "attention", // ready, busy, attention, offline, unknown
  paper: "out",      // ok, near_end, out, unknown
  cover: "closed",   // open, closed, unknown
  observedAt: "2026-09-13T20:00:00Z",
  stale: false,
  source: "device",  // windows, device, combined, unknown
  capabilities: {
    paperOut: true, paperNearEnd: false,
    jobCompletion: false, beep: true, drawer: true, cut: true
  }
}
```

Capabilities describe supported reporting/commands; unavailable observations remain `unknown`, never silently become healthy. Service connectivity and printer connectivity are separate. A service disconnect makes the POS connection indicator unavailable and existing observations stale; it does not prove every printer is offline.

A near-end warning depends on an installed sensor and supported reporting. We cannot prevent paper from running out or infer exact remaining receipts without measurements. Alert early when supported, stop new delivery on confirmed paper-out, preserve pending jobs, and reconcile already handed-off jobs after refill. Read status through Windows and its installed driver; the plugin does not open direct printer sockets or send ESC/POS status probes. USB, Ethernet and Wi-Fi share the SDK, but capabilities differ. Windows may identify a network port without distinguishing Ethernet from Wi-Fi; report that uncertainty.

### Events and the POS monitor

```javascript
const subscription = EntreePrint.subscribe(event => {
  // POS updates its shared print-monitor state; no printer polling in each view.
  updatePrintMonitor(event);
}, { events: ["connection", "printer", "job"] });

// Component/application cleanup
subscription.close();
```

Event envelope: `{ id, serviceId, type, entityId, version, occurredAt, data }`. For service-origin printer/job events, delivery is at-least-once; consumers ignore already seen entity versions. The plugin keeps a bounded durable event log for those events. The SDK reconnects with its last cursor; if history was pruned, it refreshes printers/jobs and resumes from a consistent snapshot cursor. Server event IDs must survive service restart. Local connection/heartbeat events use a separate SDK-local identity and are not added to the durable server replay cursor. Do not allow an older poll response to overwrite newer event state.

The POS print monitor's existing 30-job/30-minute view can use `getJobs()` and events. Server-wide history remains a POS server concern; one plugin's list is not a store-wide aggregate. Audit synchronization runs in the background with replay/reconciliation. Disabling the POS monitor disables that UI/audit subscription, not the plugin's minimum durable job ledger.

## 7. Device commands

```javascript
const printer = EntreePrint.target("cashier");
await printer.openDrawer({ idempotencyKey: drawerAction.id });
await printer.beep({ idempotencyKey: beepAction.id });
await printer.cut({ idempotencyKey: cutAction.id });
await printer.sendCommand(Uint8Array.from([27, 112, 0, 100, 250]), {
  idempotencyKey: customCommandAction.id
});

await kitchenTicket.print({
  idempotencyKey: kitchenPrintIntent.id,
  after: { beep: true }
});
```

`sendCommand()` accepts `Uint8Array` or an array of integer bytes (0–255); JSON transport uses base64. It does not accept JavaScript, vendor method names, or ambiguous character strings. Profiled helpers use configured model-specific bytes; the existing POS allows custom drawer kicks and documents Epson/Star differences. Unsupported operations return `COMMAND_UNSUPPORTED` before acceptance. Report command submission, not proof that a drawer physically opened.

`after` may request `beep` and a supported `cut`; no implicit drawer opening. Validate these requests before accepting the ticket. The service queues the receipt and requested trailing actions as one ordered job, preventing unrelated plugin jobs from inserting commands between them. Record delivery of individual phases: a buzzer failure must not trigger printing the entire receipt again. This ordering is not a guarantee that other programs or the Windows driver cannot intervene.

Driver-managed cutting stays in Windows Printing Preferences. Do not append a second cut when the driver already cuts. Helpers share the resolved device/queue serialization policy and use tracked Windows RAW jobs. When a receipt and trailing helper require separate Windows jobs, retain each phase's Windows job ID and outcome; the top-level spooler fields describe the receipt (or the standalone command). Do not append arbitrary RAW bytes to a GDI document. Separate spooler jobs are not atomic with respect to other applications; validate ordering against actual driver scheduling. A printer/paper fault may also delay a printer-connected drawer; expose pending status rather than promising immediate execution or bypassing an active write. Test actual model behavior before introducing command priority.

## 8. Error contract

Promise rejection is an `EntreePrintError` with `{ code, message, retryable, delivery, jobId?, details? }`. `delivery` is `not_accepted`, `accepted`, or `unknown` and describes acceptance of the request, not physical printing. Validation errors are returned before acceptance. Once accepted, device/render execution failures belong to the job record and event stream.

| Code | Expected handling |
| --- | --- |
| `SERVICE_UNAVAILABLE`, `REQUEST_TIMEOUT` | Show connection issue; retry the same key/payload within the supported window |
| `SERVICE_NOT_FOUND`, `DISCOVERY_UNAVAILABLE`, `CONNECTION_FAILED`, `NOT_CONNECTED` | No candidates for search-and-connect, no discovery transport, all discovered connections failed, or explicit disconnect; awaiting search alone returns an empty array for no results |
| `NO_PRINTERS`, `PRINTER_QUERY_FAILED` | Service is reachable but inventory is empty or could not be read; explain setup/query failure separately |
| `PRINTER_NOT_FOUND`, `PRINTER_SETTINGS_UNAVAILABLE`, `PRINTER_SETTINGS_CHANGED` | Check Windows printer settings or refresh the preview; never redirect silently to the default printer |
| `HTML_UNSUPPORTED`, `FONT_UNAVAILABLE`, `LAYOUT_OVERFLOW` | Correct the template or Windows printer preferences before submitting |
| `CONTENT_TYPE_UNSUPPORTED`, `BARCODE_FORMAT_UNSUPPORTED`, `BARCODE_VALUE_INVALID`, `CODE_TOO_DENSE` | Correct the content type, code data or available size; no image or reduced-quality substitution |
| `RENDER_EXPIRED`, `ARTIFACT_EXPIRED` | Prepare/review again or explain historical artifact retention |
| `IDEMPOTENCY_CONFLICT` | Programming/data error; never retry with a new random key to hide it |
| `CHECKSUM_MISSING`, `CHECKSUM_FORMAT_INVALID`, `CHECKSUM_INVALID` | Request rejected before acceptance; correct the SDK/packet construction and retain the original intent key |
| `REQUEST_INVALID`, `PAYLOAD_TOO_LARGE` | Invalid encoding/JSON/schema/fields or exceeded request limit; no new job created |
| `ACK_INVALID` | Acceptance cannot be confirmed from the response; reconcile/retry the same intent, never generate a replacement key |
| `QUEUE_FULL`, `STORAGE_UNAVAILABLE` | Not accepted; retain POS intent and retry after capacity/storage recovers |
| `COMMAND_UNSUPPORTED`, `RENDER_NOT_AVAILABLE` | Requested operation is not available for that target/job |
| `JOB_NOT_REPRINTABLE` | Original job is still active; reconcile it before requesting a copy |
| `JOB_NOT_FOUND`, `SERVICE_MISMATCH`, `IDEMPOTENCY_EXPIRED` | Reconcile identity/history; do not infer that it is safe to create a new print |

SDK retries only bounded transport failures with the original identity; never retry every application error. A thrown HTTP timeout may follow successful durable acceptance. A follow-up identical POST is safe by contract; creating a new key is not. If the plugin is unreachable, POS retains the intent locally and shows pending delivery. Do not report acceptance until there is a durable owner of the work.

## 9. Proposed HTTP mapping

The table below is the full target `/api` contract for the first 0.0.1 release. Consult [implementation status](API_V2_IMPLEMENTATION.md) before using a route; some exist in the working tree and others remain planned. Use SDK methods for ordinary integration. There are no compatibility routes or migration aliases; bundle the SDK with the POS.

| Route | SDK / payload |
| --- | --- |
| `GET /api/health` | Health/version/service identity, used to validate discovered candidates |
| `GET /api/heartbeat?requestId=...` | No-cache lightweight `{ requestId, serviceId, bootId, serverTime, readiness }`; cached worker readiness, no blocking printer query and no job mutation |
| `GET /api/connection` | Internal SDK handshake with server identity and inventory validation; SDK `connect()` returns `PrintLibrary`; query failure/empty inventory return documented errors |
| `GET /api/printers?refresh=true` | `getPrinters()` → array |
| `GET /api/printers/status?printer={encodedName}&refresh=true` | target `status()` |
| `POST /api/renders` | `render()`; `{ printer, html, widthMm? }` or `{ printer, content: Block[], widthMm? }` → `RenderedTicket`; exactly one of `html`/`content` |
| `POST /api/jobs` | `print()`; `{ type: "print", printer, renderId, idempotencyKey, metadata?, after? }` → `Job` |
| `POST /api/jobs` | commands; `{ printer, type, idempotencyKey, bytesBase64? }` → `Job` |
| `GET /api/jobs?...` | `getJobs()`; printer and filters as query parameters |
| `GET /api/jobs/{id}` | `getJob()` |
| `GET /api/jobs/lookup?idempotencyKey={encodedKey}` | `getJob({ idempotencyKey })`; read-only lookup on the owning service, including after layout expiry |
| `GET /api/jobs/{id}/render` | `getJobRender()` |
| `POST /api/jobs/{id}/reprints` | `reprintJob()`; `{ idempotencyKey }` |
| `GET /api/events` | resumable SSE; SDK owns reconnect/cursor mechanics |

Print without a previous render is an SDK render-then-submit sequence, not a second rendering implementation. Mutating requests require deployment authorization and the API request digest above. New job acceptance returns 202 with the durable Job; an idempotent replay returns 200 with the existing Job. Errors use `{ error: { code, message, retryable, delivery, jobId?, details? } }` with 400/401/403/404/409/410/413/422/503 as appropriate. The SDK handles checksums automatically; `/api` uses only this exact-byte checksum scheme.

## 10. Migration and release gates

Entree Print is a standalone product. Entree POS is one integration client, so its business-flow migration is separate from shipping the plugin. Standalone distribution must include the `EntreePOS/entree-print` repository, README, usage site, Windows installer and a GitHub release asset once the 0.0.1 milestone is verified.

For client applications that need local pending storage, the implemented `EntreePrint.outbox()` controller provides `enqueue`, `list`, `flush(print)`, `retry`, `cancel` and `stopRetrying`. It persists an immutable receipt intent before rendering, then the exact request before job submission. Its default IndexedDB/Web Locks store coordinates one application's browser contexts, with explicit same-service flush and no cross-node transfer. This is separate from the plugin's accepted-job ledger. See [the SDK outbox contract](sdk/README.md#durable-client-outbox) for states, operator actions, retention limits and deployment requirements.

Packet/spooler gates: corrupt one byte; missing/malformed digest; truncated upload; invalid UTF-8/JSON; duplicate keys; unsupported fields; oversized body; Chinese and escaped Unicode fixtures; valid checksum with invalid command; changed payload with recomputed checksum and reused intent key; lost or mismatched acceptance acknowledgement. Invalid new submissions must create no job or printer side effect. Test two concurrent Windows jobs, very fast completion, blocked/paper-out jobs, cancellation, spooler restart, job-ID reuse, failure during document handoff, and disappearance without terminal evidence. Assert the exact Windows job correlation and no automatic second submission after uncertain delivery. Pilot the tracking bridge with D's accepted physical quality before rollout.

| Stage | Deliverable and acceptance check |
| --- | --- |
| 1. Contract and fixtures | Agree on this document. Build anonymized real POS tickets/reports and inventory unsupported CSS, fonts, barcode and page requirements. |
| 2. Render integration | Move D preparation behind `/api/renders`; implement chain SDK, `rendered.html`, QR/Code39/Code128 blocks; compare actual POS English/Chinese layouts, void marks, prices, footer and ruler with B, and scan printed codes. |
| 3. Reliable local delivery | Durable job/key/artifact transaction, captured Windows job identity, restart recovery, bounded queue, truthful status and events. Test concurrent duplicate requests and crash windows. |
| 4. POS adapter pilot | Migrate common thermal `printHtmlTicket`/report submission and preview consumers while preserving POS routing. Accept each routed slip independently; retry only missing destinations. Keep unsupported templates on an explicitly selected C-Lodop path. |
| 5. Operational coverage | Device commands, driver-owned paper/status behavior, audit synchronization and retained-artifact reprints; unplug/reconnect, paper-out and missing-ACK tests. Pilot USB first, then Ethernet and Wi-Fi. |
| 6. Remaining templates | Long reports, label media and A4 pagination, specialized command-built slips. Replace QR PNGs with QR blocks; omit general image/logo support. Remove C-Lodop only after each required workflow passes. |

For migration, an adapter receives already selected destination/content/metadata, calls the SDK, and returns a per-slip acceptance result. Use a POS print-intent/outbox record for each slip. Preserve the current item-diff and routing functions; collect failures across destinations so an unavailable kitchen printer does not prevent recording the cashier outcome. Submit slips in order per printer and allow independent printers to progress separately. No new plugin-level business batch/routing language is needed.

Client applications must track local acceptance or an explicit pending intent. Keep business data saving independent of physical printing. One global printed flag cannot represent several destinations with different outcomes; retain destination and item-group results. Remote audit or business acknowledgements must not determine whether the plugin has accepted a receipt.

Never automatically switch from D to C-Lodop after an ambiguous submission; that can print two copies. Select fallback before physical submission after a known render/capability failure, and use one dispatch owner for that intent.

Release tests must cover: two simultaneous tickets to one printer; same key from two clients; same key/different payload; crash before/after durable acceptance and during spooler handoff; offline host; lost acceptance reply; paper-out before and during printing; partial multi-printer success; uncertain delivery without replay; double-click reprint; receipt changes after preview; expired artifacts; delayed preview responses; event disconnect/replay; custom drawer bytes; no duplicate cut; missing CJK fonts; and barcode scanning on physical tickets.

Connection tests: explicit connect includes all printers; no host, empty inventory and inventory-query failure have distinct errors; search finds zero/one/multiple services; duplicate replies deduplicate; incompatible services are rejected; discovery unavailable in a plain browser fails explicitly; broadcast-blocked Wi-Fi permits manual configuration; an address change only reconnects to the saved service ID; concurrent connect attempts cannot replace a newer selection; discovery and reconnect never submit print jobs.

Discovery/heartbeat gates: awaiting search returns all verified results or `[]`; search-and-connect chooses the first verified successful service; a failed first candidate does not prevent trying later candidates within the deadline; repeated awaits share a search; initial discovery does not authorize arbitrary cross-node routing; configured node failover follows the eligibility and ownership rules above. Validate one/two/three missed responses, delayed/out-of-order replies, cached-response rejection, process suspension/resume, service boot changes, reused IP with a different service ID, preserved printer snapshot age, shared monitoring for several subscribers, silent printer with healthy service, reconnect backoff, explicit disconnect cleanup, and no print dispatch triggered by any monitoring transition.

Code-specific tests: QR/Code39/Code128 payload round trips; leading zeros and punctuation; invalid format/data rejection; UTF-8 QR values; long payloads at narrow widths; quiet zones; adjacent HTML and readable text; whole-dot geometry at each supported DPI; preview/print artifact identity; barcode edits after preview; explicit image rejection; and physical scanning on the target printers. Vector output alone is not proof of scanner reliability.

Use synthetic fixtures for development and verification. Clients must send only the receipt content and metadata they intend to retain; do not include authentication credentials or unnecessary payment data in saved artifacts.

## 11. What exists today

| Area | Current implementation | This proposal |
| --- | --- | --- |
| Main SDK | Fluent config/connect/search/target/render/print, history and reprints | Live fault pilots and POS integration |
| Main jobs | Durable file-backed jobs, captured Windows attempts, completion monitoring, configurable artifact expiry and retained deduplication records | Long-term history export and physical fault pilot |
| D demo | Port 19779: session, prepare, SVG preview, A/D print; in-memory IDs | Integrated `render()` returning HTML and retained job artifacts |
| Rendering | D handles a restricted HTML subset | Real POS fixture coverage before broad rollout |
| Reprints | Retained accepted artifact, new intent, linked job and active-original guard | Operator uncertainty flow in POS and physical pilot |
| POS monitor | Server audit calls and client-side C-Lodop polling/reconciliation | Plugin-owned delivery state plus background POS audit sync |

The SDK and HTTP implementation status are listed separately from this target contract. The Wi-Fi regression harness uses fakes; virtual-spooler and process-termination evidence is documented in the beta checklist. Representative standalone client integration and physical fault pilots remain release requirements; migration of a specific client's business flows is separate.
