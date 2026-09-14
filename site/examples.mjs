export const examples = {
  connect: `import EntreePrint from './sdk/entree-print.mjs';

const print = await EntreePrint.config({
  ip: '127.0.0.1',
  port: 9779,
  token: settings.printServiceToken
}).connect();

const printers = await print.getPrinters();
const cashier = print.target(printers[0].name);`,
  preview: `const ticket = print.target('cashier').setContent(
  '<h2>Order 1042</h2><p>炒饭 Fried rice × 2</p>'
);

const rendered = await ticket.render();
previewFrame.setAttribute('sandbox', '');
previewFrame.srcdoc = rendered.html;

// On the operator's print action, reuse this ticket.
const job = await ticket.print({
  idempotencyKey: savedIntent.id,
  metadata: { orderID: '1042', station: 'COUNTER-1' }
});`,
  codes: `const ticket = print.target('cashier').setContent([
  { type: 'html', html: '<h2>Order 1042 · 谢谢</h2>' },
  { type: 'qrcode', value: 'ORDER1042', sizeMm: 24 },
  {
    type: 'barcode',
    format: 'code128',
    value: 'ORDER1042',
    showText: true
  }
]);

const rendered = await ticket.render();
previewFrame.srcdoc = rendered.html;`,
  discovery: `// Node or Electron main; browsers cannot broadcast UDP.
import EntreePrint from './sdk/native.mjs';

EntreePrint.config({ token: settings.printServiceToken });

const servers = await EntreePrint.search();
const print = await EntreePrint.search().connect();
const printers = await print.getPrinters();`,
  status: `const status = await print.target('cashier')
  .status({ refresh: true });

const page = await print.getJobs('cashier', {
  station: 'COUNTER-1',
  orderID: '1042',
  limit: 30
});

const job = await print.getJob(savedJob.id);
const originalPreview = await print.getJobRender(job.id);`,
  recovery: `// Reconnect to the saved owning service, then reconcile.
const print = await EntreePrint.connect({
  ip: settings.printHost,
  port: 9779,
  serviceId: savedIntent.serviceId,
  token: settings.printServiceToken
});

const job = await print.getJob({
  idempotencyKey: savedIntent.id
});

// A missing result is not proof that an earlier request
// is no longer in flight. Keep the same intent key.`,
  outbox: `const outbox = EntreePrint.outbox();

// Save before network work, using the previously saved owner.
await outbox.enqueue({
  idempotencyKey: savedIntent.id,
  serviceId: savedIntent.serviceId,
  printer: 'cashier',
  content: '<p>Order 1042 · 谢谢</p>'
});

// Call again after reconnect or application startup.
const results = await outbox.flush(print);
const localIntents = await outbox.list();`,
  commands: `const cashier = print.target('cashier');

await cashier.openDrawer({ idempotencyKey: drawerAction.id });
await cashier.beep({ idempotencyKey: beepAction.id });
await cashier.cut({ idempotencyKey: cutAction.id });

// Each action needs verified device bytes in service settings.
// Windows driver cutting is used for ordinary receipts.`,
  build: `dotnet test EntreePrintPlugin.Tests/EntreePrintPlugin.Tests.csproj --artifacts-path "$env:TEMP\\EntreePrintTests"
node --test sdk/test/*.test.mjs
powershell -NoProfile -File scripts/publish.ps1 -OutputRoot ./dist/my-beta-build`
};
