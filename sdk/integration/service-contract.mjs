// The .NET test runner connects this SDK fetch boundary to the actual ASP.NET
// TestServer through stdio. No TCP listener or physical printer is used.
import assert from 'node:assert/strict';
import { createInterface } from 'node:readline';
import { webcrypto } from 'node:crypto';
import { mkdtemp, readFile, open, rename, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createEntreePrint } from '../entree-print.mjs';

const input = createInterface({ input: process.stdin });
let sequence = 0;
const pending = new Map();
input.on('line', line => {
  const message = JSON.parse(line);
  const waiter = pending.get(message.id);
  if (!waiter) throw new Error('Unexpected bridge response');
  pending.delete(message.id);
  if (message.error) waiter.reject(new Error(message.error));
  else waiter.resolve(message);
});
function exchange(message) {
  const id = ++sequence;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    process.stdout.write(JSON.stringify({ id, ...message }) + '\n');
  });
}
const control = (control, extra = {}) => exchange({ control, ...extra });
const jobWires = [];
let faults = [];
let corruptNextRequest = false;
let renderCalls = 0;
function createClient(outboxStorage) { return createEntreePrint({ token: 't'.repeat(32), retryCount: 1, retryDelayMs: 1,
  requestTimeoutMs: 15000, renderTimeoutMs: 30000, heartbeat: { intervalMs: 60000 } }, {
  crypto: webcrypto,
  outboxStorage,
  fetch: async (url, options) => {
    const path = new URL(url).pathname;
    let bytes = options.body === undefined ? null : Buffer.from(options.body);
    if (path === '/api/renders') renderCalls++;
    if (path === '/api/jobs' && options.method === 'POST') {
      jobWires.push(bytes.toString('base64'));
      if (corruptNextRequest) {
        corruptNextRequest = false;
        bytes = Buffer.concat([bytes, Buffer.from(' ')]); // Valid JSON, wrong exact-byte digest.
      }
    }
    const result = await exchange({ url, method: options.method, headers: options.headers,
      bodyBase64: bytes?.toString('base64') ?? null });
    if (path === '/api/jobs' && options.method === 'POST' && result.status < 300) {
      const fault = faults.shift();
      if (fault === 'lost') throw new TypeError('Simulated lost acknowledgement after durable acceptance');
      if (fault === 'digest') result.headers['X-Entree-Content-SHA256'] = '0'.repeat(64);
    }
    return new Response(Buffer.from(result.bodyBase64, 'base64'), { status: result.status, headers: result.headers });
  }
}); }
const api = createClient();

const kitchen = '厨房 A / 热菜';
const html = '<p style="font:16px Arial,Microsoft YaHei,sans-serif;margin:0">欢迎 café — 订单42</p>';
const metadata = { station: '平板 1', orderID: '订单42', itemNames: ['炒饭', 'café'] };
const waitState = (key, state) => control('waitState', { key, state });

async function lostAck() {
  const print = await api.connect();
  const connection = print.connection;
  const ticket = print.target(kitchen).setContent([
    { type: 'html', html }, { type: 'qrcode', value: '订单42' },
    { type: 'barcode', format: 'code128', value: 'ORDER42' }
  ]);
  const preview = await ticket.render();
  assert.equal(preview.serviceId, connection.serviceId);
  assert.match(preview.html, /<svg/);
  assert.match(preview.html, /欢迎/);
  assert.equal((await control('inspect')).jobs.length, 0);
  faults = ['lost', 'lost'];
  await assert.rejects(ticket.print({ idempotencyKey: 'lost-ack', metadata }), error => {
    assert.equal(error.code, 'SERVICE_UNAVAILABLE');
    assert.equal(error.delivery, 'unknown');
    assert.equal(error.details.idempotencyKey, 'lost-ack');
    return true;
  });
  await waitState('lost-ack', 'completed');
  const before = await control('inspect');
  assert.equal(before.jobs.length, 1);
  assert.equal(before.deliveries.length, 1);
  const restarted = await control('restart', { expirePreviews: true });
  assert.equal(restarted.serviceId, connection.serviceId);
  assert.notEqual(restarted.bootId, connection.bootId);
  await api.connect();
  const recovered = await ticket.print({ idempotencyKey: 'lost-ack', metadata });
  assert.equal(recovered.state, 'completed');
  assert.equal(recovered.id, before.jobs[0].id);
  assert.equal(renderCalls, 1);
  assert.equal(jobWires.length, 3);
  assert.equal(new Set(jobWires).size, 1);
  assert.equal((await api.getJobRender(recovered.id)).html, preview.html);
  const history = await api.getJobs(kitchen, { station: metadata.station, orderID: metadata.orderID });
  assert.equal(history.items.length, 1);
  assert.deepEqual(history.items[0].metadata, metadata);
  assert.equal((await api.getJob(recovered.id)).id, recovered.id);
  assert.equal((await control('inspect')).deliveries.length, 1);
  const reprinted = await api.reprintJob(recovered.id, { idempotencyKey: 'operator-reprint' });
  assert.equal(reprinted.reprintOf, recovered.id);
  await waitState('operator-reprint', 'completed');
  assert.equal((await api.reprintJob(recovered.id, { idempotencyKey: 'operator-reprint' })).id, reprinted.id);
  const final = await control('inspect');
  assert.equal(final.jobs.length, 2);
  assert.equal(final.deliveries.length, 2);
}

async function offlineDestination() {
  await control('offline', { printer: kitchen, offline: true });
  const print = await api.connect();
  assert.equal((await print.getPrinters()).length, 2);
  const customer = print.target('Kitchen').setContent(html);
  const kitchenTicket = print.target(kitchen).setContent(html);
  const [receipt, prep] = await Promise.all([customer.render(), kitchenTicket.render()]);
  assert.notEqual(receipt.id, prep.id);
  const [cashierJob, kitchenJob] = await Promise.all([
    customer.print({ idempotencyKey: 'cashier', metadata }),
    kitchenTicket.print({ idempotencyKey: 'kitchen', metadata })
  ]);
  await waitState('cashier', 'completed');
  await waitState('kitchen', 'waiting_for_printer');
  assert.equal((await api.target(kitchen).status()).offline, true);
  const waiting = await api.getJobs(kitchen, { status: 'waiting_for_printer' });
  assert.deepEqual(waiting.items.map(job => job.id), [kitchenJob.id]);
  assert.equal((await api.getJob(cashierJob.id)).state, 'completed');
  let inspected = await control('inspect');
  assert.equal(inspected.deliveries.length, 1);
  assert.equal(inspected.jobs.find(job => job.key === 'kitchen').attempts, 0);
  await control('restart');
  await api.connect();
  await waitState('kitchen', 'waiting_for_printer');
  assert.equal((await api.getJobRender(kitchenJob.id)).html, prep.html);
  await control('offline', { printer: kitchen, offline: false });
  await waitState('kitchen', 'completed');
  assert.equal((await kitchenTicket.print({ idempotencyKey: 'kitchen', metadata })).id, kitchenJob.id);
  inspected = await control('inspect');
  assert.equal(inspected.jobs.length, 2);
  assert.equal(inspected.deliveries.length, 2);
  assert.deepEqual(inspected.deliveries.map(item => item.key).sort(), ['cashier', 'kitchen']);
}

async function integrity() {
  await api.connect();
  const ticket = api.target(kitchen).setContent(html);
  await ticket.render();
  corruptNextRequest = true;
  await assert.rejects(ticket.print({ idempotencyKey: 'integrity' }), { code: 'CHECKSUM_INVALID', delivery: 'not_sent' });
  assert.equal((await control('inspect')).jobs.length, 0);
  assert.equal(jobWires.length, 1);
  faults = ['digest', 'digest'];
  await assert.rejects(ticket.print({ idempotencyKey: 'integrity' }), { code: 'ACK_INVALID', delivery: 'unknown' });
  await waitState('integrity', 'completed');
  assert.equal((await control('inspect')).deliveries.length, 1);
  const job = await ticket.print({ idempotencyKey: 'integrity' });
  assert.equal(job.state, 'completed');
  assert.equal(renderCalls, 1);
  assert.equal(new Set(jobWires).size, 1);
  assert.equal((await control('inspect')).deliveries.length, 1);
}

async function retention() {
  const print = await api.connect();
  assert.equal(print.connection.retention.receiptDays, 7);
  const ticket = print.target(kitchen).setContent(html);
  const original = await ticket.print({ idempotencyKey: 'retained-original', metadata });
  await waitState('retained-original', 'completed');
  assert.equal((await control('expireReceipts')).expired, 1);
  await control('restart');
  await api.connect();
  const replay = await ticket.print({ idempotencyKey: 'retained-original', metadata });
  assert.equal(replay.id, original.id);
  assert.equal(replay.state, 'completed');
  assert.ok(replay.artifactExpiredAt);
  assert.equal(new Set(jobWires).size, 1);
  assert.equal(renderCalls, 1);
  await assert.rejects(print.getJobRender(original.id), { code: 'ARTIFACT_EXPIRED' });
  await assert.rejects(print.reprintJob(original.id, { idempotencyKey: 'expired-copy' }), { code: 'ARTIFACT_EXPIRED' });
  const history = await print.getJobs(kitchen, { station: metadata.station });
  assert.equal(history.items.length, 1);
  assert.equal(history.items[0].id, original.id);
  assert.equal((await control('inspect')).deliveries.length, 1);
}

async function keyLookupAfterReload() {
  const print = await api.connect();
  const intent = { idempotencyKey: 'persisted-key-after-reload', serviceId: print.connection.serviceId };
  await control('saveClientIntent', { intent });
  faults = ['lost', 'lost'];
  await assert.rejects(print.target(kitchen).setContent(html).print({ idempotencyKey: intent.idempotencyKey, metadata }),
    { code: 'SERVICE_UNAVAILABLE', delivery: 'unknown' });
  await waitState(intent.idempotencyKey, 'completed');
  await control('expireReceipts');
  api.disconnect();
  await control('restart');
  const fresh = createClient();
  try {
    const saved = (await control('readClientIntent')).intent;
    const restored = await fresh.connect({ ip: '127.0.0.1', serviceId: saved.serviceId });
    const writes = jobWires.length, renders = renderCalls;
    const job = await restored.getJob({ idempotencyKey: saved.idempotencyKey });
    assert.equal(job.idempotencyKey, saved.idempotencyKey);
    assert.equal(job.serviceId, saved.serviceId);
    assert.equal(job.state, 'completed');
    assert.ok(job.artifactExpiredAt);
    assert.equal(jobWires.length, writes);
    assert.equal(renderCalls, renders);
    assert.equal((await control('inspect')).deliveries.length, 1);
  } finally { fresh.disconnect(); }
}

async function durableClientOutbox() {
  const directory = await mkdtemp(join(tmpdir(), 'EntreeClientOutbox-'));
  const file = join(directory, 'intents.json');
  // Test adapter only: one Node dispatcher; production browser storage has Web Locks.
  function storage() {
    const list = async () => { try { return JSON.parse(await readFile(file, 'utf8')); } catch (error) { if (error.code === 'ENOENT') return []; throw error; } };
    const save = async records => {
      const temp = file + '.tmp'; const handle = await open(temp, 'wx');
      try { await handle.writeFile(JSON.stringify(records)); await handle.sync(); } finally { await handle.close(); }
      await rename(temp, file);
    };
    return {
      list, withQueue: async (_service, _printer, work) => work(),
      async add(record) {
        const records = await list(); const prior = records.find(row => row.serviceId === record.serviceId && row.idempotencyKey === record.idempotencyKey);
        if (prior) return prior;
        const saved = { ...record, localId: records.length + 1, version: 1 }; await save([...records, saved]); return saved;
      },
      async update(previous, record) {
        const records = await list(); const at = records.findIndex(row => row.localId === previous.localId);
        assert.equal(records[at].version, previous.version);
        records[at] = { ...record, version: previous.version + 1 }; await save(records); return records[at];
      }
    };
  }
  const clients = [];
  try {
    const setup = await api.connect();
    const owner = setup.connection.serviceId; api.disconnect();
    const first = createClient(storage()); clients.push(first);
    await first.outbox().enqueue({ serviceId: owner, idempotencyKey: 'durable-client-outbox', printer: kitchen, content: html, metadata });
    assert.equal(renderCalls, 0); assert.equal(jobWires.length, 0); first.disconnect();
    const second = createClient(storage()); clients.push(second);
    faults = ['lost', 'lost'];
    const uncertain = await second.outbox().flush(await second.connect({ serviceId: owner }));
    assert.equal(uncertain[0].state, 'uncertain'); await waitState('durable-client-outbox', 'completed');
    second.disconnect();
    await control('expireReceipts'); await control('restart', { expirePreviews: true });
    const third = createClient(storage()); clients.push(third);
    const recovered = await third.outbox().flush(await third.connect({ serviceId: owner }));
    assert.equal(recovered[0].state, 'accepted'); assert.equal(recovered[0].job.state, 'completed');
    assert.equal(renderCalls, 1); assert.equal(new Set(jobWires).size, 1);
    assert.equal((await control('inspect')).deliveries.length, 1);
    assert.equal((await third.outbox().list())[0].job.id, recovered[0].job.id);
  } finally {
    clients.forEach(client => client.disconnect());
    assert.ok(directory.startsWith(join(tmpdir(), 'EntreeClientOutbox-')));
    await rm(directory, { recursive: true, force: true });
  }
}

try {
  const scenario = process.argv[2];
  if (scenario === 'lost-ack') await lostAck();
  else if (scenario === 'offline') await offlineDestination();
  else if (scenario === 'integrity') await integrity();
  else if (scenario === 'retention') await retention();
  else if (scenario === 'key-lookup') await keyLookupAfterReload();
  else if (scenario === 'outbox') await durableClientOutbox();
  else throw new Error(`Unknown scenario: ${scenario}`);
  process.stdout.write(JSON.stringify({ done: true, scenario }) + '\n');
} catch (error) {
  process.stderr.write((error.stack ?? String(error)) + '\n');
  process.exitCode = 1;
} finally {
  api.disconnect();
  input.close();
  process.stdin.destroy();
}
