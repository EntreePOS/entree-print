import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, webcrypto } from 'node:crypto';
import { createEntreePrint } from '../entree-print.mjs';
import { Clock, flush } from './helpers.mjs';

const token = 'test-only-token-'.repeat(3);
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const json = (body, status = 200, headers = {}) => new Response(JSON.stringify(body), { status, headers });
const rejected = (code, delivery = 'not_sent') => json({ error: { code, message: code, delivery, retryable: false } }, 422);
test('default browser timers retain their global receiver on connect and disconnect', async t => {
  const originalSet = globalThis.setTimeout, originalClear = globalThis.clearTimeout;
  let scheduled = 0, cleared = 0;
  t.mock.method(globalThis, 'setTimeout', function (...args) {
    assert.equal(this, globalThis, 'Browser setTimeout requires the Window receiver.');
    scheduled++; return Reflect.apply(originalSet, globalThis, args);
  });
  t.mock.method(globalThis, 'clearTimeout', function (...args) {
    assert.equal(this, globalThis, 'Browser clearTimeout requires the Window receiver.');
    cleared++; return Reflect.apply(originalClear, globalThis, args);
  });
  const api = createEntreePrint({ token }, { fetch: async () => json({ serviceId:'timer-test', bootId:'boot-1', apiVersion:'0.0.1', printers:[{name:'cashier'}] }) });
  try { await api.connect(); } finally { api.disconnect(); }
  assert.ok(scheduled >= 2); assert.ok(cleared >= 2);
});
function harness(t, options = {}) {
  const clock = new Clock();
  const calls = []; const jobs = new Map(); let renderCount = 0;
  const state = { hook: null, heartbeat: null, identity: null, boot: 'boot-1', clock, calls, jobs };
  async function fetch(url, init) {
    const address = new URL(url); const serviceId = state.identity ?? address.hostname;
    const call = { url: address, init, body: init.body && JSON.parse(new TextDecoder().decode(init.body)) };
    calls.push(call);
    if (state.hook) { const response = await state.hook(call); if (response) return response; }
    if (address.pathname === '/api/connection') return json({ serviceId, bootId: state.boot, apiVersion: '0.0.1', printers: [{ name: 'Kitchen' }] });
    if (address.pathname === '/api/printers') return json([{ name: 'Kitchen' }]);
    if (address.pathname === '/api/heartbeat') {
      if (state.heartbeat) return state.heartbeat(call);
      return json({ serviceId, bootId: state.boot, requestId: address.searchParams.get('requestId') });
    }
    if (init.body) {
      assert.equal(init.headers['X-Entree-Content-SHA256'], digest(init.body));
      assert.equal(init.headers['X-Entree-Service-ID'], serviceId);
      assert.equal(init.headers.Authorization, `Bearer ${token}`);
      assert.equal(init.redirect, 'error');
    }
    const headers = { 'X-Entree-Content-SHA256': init.headers['X-Entree-Content-SHA256'] ?? '' };
    if (address.pathname === '/api/renders') return json({ id: `render-${++renderCount}`, serviceId, html: '<html>欢迎</html>',
      widthMm: 72, expiresAt: new Date(clock.now + 10000).toISOString() }, 200, headers);
    const reprint = /^\/api\/jobs\/([^/]+)\/reprints$/.exec(address.pathname);
    if (address.pathname === '/api/jobs' || reprint) {
      const body = call.body;
      const prior = jobs.get(body.idempotencyKey);
      if (prior && JSON.stringify(prior.body) !== JSON.stringify(body)) return rejected('IDEMPOTENCY_CONFLICT');
      const result = prior?.result ?? { id: `job-${jobs.size + 1}`, serviceId, idempotencyKey: body.idempotencyKey,
        integrity: { verified: true, requestDigest: headers['X-Entree-Content-SHA256'] }, state: 'accepted', delivery: { state: 'not_sent' },
        ...(reprint ? { reprintOf: decodeURIComponent(reprint[1]) } : {}) };
      jobs.set(body.idempotencyKey, { body, result });
      return json(result, prior ? 200 : 202, headers);
    }
    throw new Error(`Unexpected route ${address.pathname}`);
  }
  const api = createEntreePrint({ token, retryDelayMs: 0, ...options }, { fetch, crypto: webcrypto,
    now: () => clock.now, setTimeout: clock.setTimeout, clearTimeout: clock.clearTimeout });
  t.after(() => api.disconnect());
  return { api, ...state, state, clock, calls, jobs };
}

test('connect accepts a settings object or discovery record, preserving existing ticket destinations', async t => {
  const { api, calls } = harness(t);
  const old = api.target('Kitchen').setContent('<p>Original</p>');
  const server = { ip: 'backup-host', port: 9779, serviceId: 'backup-host', name: 'Backup', protocol: 'http' };
  const pending = api.connect(server);
  server.ip = 'mutated-host';
  assert.equal((await pending).connection.serviceId, 'backup-host');
  await api.target('Kitchen').setContent('<p>New</p>').print({ idempotencyKey: 'new' });
  await old.print({ idempotencyKey: 'old' });
  assert.equal(calls.find(call => call.body?.idempotencyKey === 'new').url.hostname, 'backup-host');
  assert.equal(calls.find(call => call.body?.idempotencyKey === 'old').url.hostname, '127.0.0.1');
  assert.equal((await api.connect({ ip: 'third-host', port: 9780 })).connection.port, 9780);
});

test('connect rejects a discovery record whose service identity changed', async t => {
  const { api } = harness(t);
  await assert.rejects(api.connect({ ip: 'replacement-host', serviceId: 'expected-service' }), { code: 'SERVICE_MISMATCH' });
  assert.throws(() => api.connect({ ip: 'unused', serviceId: '' }), { code: 'REQUEST_INVALID' });
});

test('getJob accepts a persisted intent key and keeps lookup on the connected library owner', async t => {
  const { api, state, calls } = harness(t);
  const print = await api.connect();
  await api.connect({ ip: 'another-service' });
  state.hook = call => call.url.pathname === '/api/jobs/lookup' ? json({ id: 'original', serviceId: '127.0.0.1',
    idempotencyKey: call.url.searchParams.get('idempotencyKey'), state: 'waiting_for_printer' }) : null;
  const selection = { idempotencyKey: '订单 / + # & %2F' };
  const pending = print.getJob(selection);
  selection.idempotencyKey = 'changed';
  const job = await pending;
  assert.equal(job.id, 'original');
  assert.equal(job.state, 'waiting_for_printer');
  assert.equal(job.idempotencyKey, '订单 / + # & %2F');
  assert.equal(calls.at(-1).url.hostname, '127.0.0.1');
  assert.equal(calls.filter(call => call.init.method === 'POST').length, 0);
});

test('failed key lookup cannot establish the delivery outcome of an earlier print', async t => {
  const { api, state, calls } = harness(t);
  await api.connect();
  state.hook = () => json({ error: { code: 'JOB_NOT_FOUND', delivery: 'not_sent' } }, 404);
  await assert.rejects(api.getJob({ idempotencyKey: 'unknown-key' }), error => {
    assert.equal(error.code, 'JOB_NOT_FOUND');
    assert.equal(error.delivery, 'unknown');
    assert.equal(error.details.operation, 'lookup');
    assert.equal(error.details.idempotencyKey, 'unknown-key');
    return true;
  });
  state.hook = () => { throw new TypeError('network lost'); };
  await assert.rejects(api.getJob({ idempotencyKey: 'unknown-key' }), { code: 'SERVICE_UNAVAILABLE', delivery: 'unknown' });
  assert.equal(calls.filter(call => call.init.method === 'POST').length, 0);
});

test('key lookup rejects wrong-key and incomplete results', async t => {
  const { api, state } = harness(t);
  await api.connect();
  for (const result of [{ id: 'job', serviceId: '127.0.0.1', idempotencyKey: 'wrong' }, { id: 'job', idempotencyKey: 'wanted' },
    { id: '', serviceId: '127.0.0.1', idempotencyKey: 'wanted' }]) {
    state.hook = () => json(result);
    await assert.rejects(api.getJob({ idempotencyKey: 'wanted' }), { code: 'RESPONSE_INVALID', delivery: 'unknown' });
  }
});

test('invalid lookup selectors fail without requests', t => {
  const { api, calls } = harness(t);
  for (const selector of [null, {}, { idempotencyKey: '' }, { idempotencyKey: '   ' }, { idempotencyKey: 'x'.repeat(201) },
    { idempotencyKey: 'key', printer: 'Kitchen' }])
    assert.throws(() => api.getJob(selector), error => ['REQUEST_INVALID', 'FIELD_UNSUPPORTED'].includes(error.code));
  assert.equal(calls.length, 0);
});

test('returned libraries keep printer queries, new tickets and history on their own server', async t => {
  const { api, calls, state } = harness(t);
  const first = await api.connect();
  const second = await api.connect({ ip: 'second-host' });
  state.hook = call => !call.body && call.url.pathname.startsWith('/api/jobs') ? json({ items: [], id: 'saved' }) : null;
  const start = calls.length;
  // Create the first library's target after the default connection has changed.
  await first.getPrinters({ refresh: true });
  await first.getJobs('Kitchen', { limit: 10 });
  await first.getJob('saved');
  await first.getJobRender('saved');
  await first.reprintJob('saved', { idempotencyKey: 'first-copy' });
  await first.target('Kitchen').setContent('<p>First server</p>').print({ idempotencyKey: 'first-library' });
  assert.ok(calls.slice(start).every(call => call.url.hostname === '127.0.0.1'));
  assert.equal(calls[start].url.searchParams.get('refresh'), 'true');
  await second.getPrinters();
  assert.equal(calls.at(-1).url.hostname, 'second-host');
  const connection = first.connection;
  connection.serviceId = 'changed';
  assert.equal(first.connection.serviceId, '127.0.0.1');
  assert.equal(Object.isFrozen(first), true);
});

test('a late connection returns the library for its captured server', async t => {
  const { api, state, calls } = harness(t);
  let release;
  state.hook = call => call.url.pathname === '/api/connection' && call.url.hostname === '127.0.0.1'
    ? new Promise(resolve => { release = resolve; }) : null;
  const pending = api.connect(); await flush();
  const second = await api.connect({ ip: 'second-host' });
  release(json({ serviceId: '127.0.0.1', bootId: 'boot', apiVersion: '0.0.1', printers: [{ name: 'Kitchen' }] }));
  const first = await pending;
  await first.getPrinters(); assert.equal(calls.at(-1).url.hostname, '127.0.0.1');
  await second.getPrinters(); assert.equal(calls.at(-1).url.hostname, 'second-host');
  await api.getPrinters(); assert.equal(calls.at(-1).url.hostname, 'second-host');
});

test('connect rejects an empty inventory and disconnected libraries cannot print', async t => {
  const { api, state, calls } = harness(t);
  state.hook = () => json({ serviceId: '127.0.0.1', bootId: 'boot', apiVersion: '0.0.1', printers: [] });
  await assert.rejects(api.connect(), { code: 'NO_PRINTERS' });
  state.hook = null;
  const print = await api.connect();
  api.disconnect();
  const count = calls.length;
  await assert.rejects(print.getPrinters(), { code: 'NOT_CONNECTED' });
  await assert.rejects(print.target('Kitchen').setContent('receipt').print(), { code: 'NOT_CONNECTED' });
  assert.equal(calls.length, count);
});

test('history encodes filters and paged results without mutating caller state', async t => {
  const { api, state, calls } = harness(t);
  state.hook = call => call.url.pathname === '/api/jobs' && !call.body
    ? json({ items: [{ id: 'old-job' }], nextCursor: 'opaque+cursor' }) : null;
  const filters = { station: '收银 & 1', orderID: '42/5', status: ['failed', 'needs_attention'], since: '2026-09-13T00:00:00Z', limit: 30 };
  const pending = api.getJobs('Kitchen & Bar', filters);
  filters.status.push('completed'); filters.station = 'changed';
  const page = await pending;
  assert.equal(page.items[0].id, 'old-job');
  const query = calls.find(call => call.url.pathname === '/api/jobs').url.searchParams;
  assert.equal(query.get('station'), '收银 & 1');
  assert.equal(query.get('printer'), 'Kitchen & Bar');
  assert.equal(query.get('orderID'), '42/5');
  assert.deepEqual(query.getAll('status'), ['failed', 'needs_attention']);
  await api.getJobs(undefined, { cursor: page.nextCursor, limit: 100 });
  assert.equal(calls.at(-1).url.searchParams.get('cursor'), 'opaque+cursor');
  for (const invalid of [{ limit: 101 }, { limit: 1.5 }, { status: ['printed'] }, { since: '2026-09-13' }, { offset: 1 }])
    assert.throws(() => api.getJobs(undefined, invalid), error => ['REQUEST_INVALID', 'FIELD_UNSUPPORTED'].includes(error.code));
});

test('explicit reprint retries identical bytes and validates its original job', async t => {
  const { api, state, calls, clock, jobs } = harness(t);
  let first;
  state.hook = call => {
    if (call.url.pathname.endsWith('/reprints') && !first) { first = call; throw new TypeError('lost ACK'); }
  };
  const pending = api.reprintJob('old-job', { idempotencyKey: 'operator-copy' });
  while (!first) await new Promise(resolve => setImmediate(resolve));
  await flush(); await clock.advance(0);
  const result = await pending;
  assert.equal(result.reprintOf, 'old-job');
  assert.equal(jobs.size, 1);
  const writes = calls.filter(call => call.url.pathname.endsWith('/reprints'));
  assert.equal(writes.length, 2);
  assert.deepEqual(writes[0].init.body, writes[1].init.body);
  assert.deepEqual(writes[0].body, { idempotencyKey: 'operator-copy' });
  assert.equal(calls.filter(call => call.url.pathname === '/api/renders').length, 0);
});

test('reprint network loss and a wrong-parent ACK remain unknown, retaining the supplied key', async t => {
  const { api, state } = harness(t, { retryCount: 0 });
  await assert.rejects(api.reprintJob('original'), { code: 'REQUEST_INVALID' });
  state.hook = call => {
    if (call.url.pathname.endsWith('/reprints')) throw new TypeError('connection lost');
  };
  await assert.rejects(api.reprintJob('original', { idempotencyKey: 'copy' }), error => {
    assert.equal(error.delivery, 'unknown'); assert.equal(error.details.idempotencyKey, 'copy'); return true;
  });
  state.hook = call => call.url.pathname.endsWith('/reprints') ? json({ id: 'copy', serviceId: '127.0.0.1',
    idempotencyKey: 'copy', reprintOf: 'wrong-original', integrity: { verified: true } }, 202,
    { 'X-Entree-Content-SHA256': digest(call.init.body) }) : null;
  await assert.rejects(api.reprintJob('original', { idempotencyKey: 'copy' }), { code: 'ACK_INVALID', delivery: 'unknown' });
});

test('reprint captures the selected service before asynchronous encoding', async t => {
  const { api, calls } = harness(t);
  await api.connect();
  const pending = api.reprintJob('original', { idempotencyKey: 'copy' });
  api.config({ ip: 'another-service' });
  await pending;
  assert.equal(calls.find(call => call.url.pathname.endsWith('/reprints')).url.hostname, '127.0.0.1');
});

test('config and builders are lazy; connected library retrieves printers and immutable targets retain their endpoint', async t => {
  const { api, calls } = harness(t);
  const first = api.target('Kitchen').setContent('<p>First</p>').setWidth(72);
  assert.equal(calls.length, 0);
  const print = await api.connect(); assert.equal((await print.getPrinters())[0].name, 'Kitchen');
  assert.equal('printers' in print, false);
  assert.equal('printers' in print.connection, false);
  api.config({ ip: 'new-host' }); await api.connect();
  await first.print({ idempotencyKey: 'first' });
  await api.target('Kitchen').setContent('<p>Second</p>').setWidth(72).print({ idempotencyKey: 'second' });
  assert.equal(calls.find(c => c.body?.idempotencyKey === 'first').url.hostname, '127.0.0.1');
  assert.equal(calls.find(c => c.body?.idempotencyKey === 'second').url.hostname, 'new-host');
});

test('concurrent renders share preparation; caller mutations cannot change content or cached HTML', async t => {
  const { api, calls, jobs } = harness(t);
  const blocks = [{ type: 'html', html: '<p>欢迎 café</p>' }, { type: 'qrcode', value: '订单42' }];
  const ticket = api.target('Kitchen').setWidth(72).setContent(blocks);
  blocks[0].html = '<p>changed</p>';
  const [a, b] = await Promise.all([ticket.render(), ticket.render()]);
  assert.equal(a.id, b.id); a.id = 'forged'; a.html = 'modified';
  assert.equal((await ticket.render()).id, b.id);
  assert.equal(calls.filter(c => c.url.pathname === '/api/renders').length, 1);
  assert.equal(calls.find(c => c.body?.content).body.content[0].html, '<p>欢迎 café</p>');
  assert.equal(jobs.size, 0);
  await ticket.print(); await ticket.print();
  assert.equal(jobs.size, 1);
  assert.equal([...jobs.values()][0].body.renderId, b.id);
});

test('changed ticket snapshots have independent generated print intents', async t => {
  const { api, jobs } = harness(t);
  const original = api.target('Kitchen').setContent('<p>original</p>').setWidth(72);
  const changed = original.setContent('<p>changed</p>');
  const widths = original.setWidth(58);
  await Promise.all([original.print(), changed.print(), widths.print()]);
  assert.equal(jobs.size, 3);
});

test('concurrent print calls share one submission and metadata is copied immediately', async t => {
  const { api, calls } = harness(t);
  const ticket = api.target('Kitchen').setContent('<p>Receipt</p>').setWidth(72);
  const metadata = { station: 'POS-1', itemNames: ['rice'] };
  const one = ticket.print({ idempotencyKey: 'intent', metadata });
  const two = ticket.print({ idempotencyKey: 'intent', metadata });
  metadata.itemNames.push('changed');
  const results = await Promise.all([one, two]);
  assert.equal(results[0].id, results[1].id);
  assert.equal(calls.filter(c => c.url.pathname === '/api/jobs').length, 1);
  assert.deepEqual(calls.find(c => c.body?.type === 'print').body.metadata.itemNames, ['rice']);
  await assert.rejects(ticket.print({ idempotencyKey: 'intent', metadata }), { code: 'IDEMPOTENCY_CONFLICT' });
});

test('lost ACK retry sends identical bytes, digest, key and render without preparing another copy', async t => {
  const { api, state, calls, clock } = harness(t);
  let first;
  state.hook = call => {
    if (call.url.pathname === '/api/jobs' && !first) { first = call; throw new TypeError('connection lost after submission'); }
  };
  const ticket = api.target('Kitchen').setContent('<p>欢迎</p>').setWidth(72);
  await ticket.render();
  const pending = ticket.print({ idempotencyKey: 'persisted-key' });
  // Real Web Crypto completion yields to the event loop; the retry delay uses our controlled clock.
  while (!first) await new Promise(resolve => setImmediate(resolve));
  await flush(); await clock.advance(0);
  const result = await pending;
  const writes = calls.filter(c => c.url.pathname === '/api/jobs');
  assert.equal(writes.length, 2); assert.deepEqual(writes[0].init.body, writes[1].init.body);
  assert.equal(writes[0].init.headers['X-Entree-Content-SHA256'], writes[1].init.headers['X-Entree-Content-SHA256']);
  assert.equal(result.idempotencyKey, 'persisted-key');
  assert.equal(calls.filter(c => c.url.pathname === '/api/renders').length, 1);
});

test('invalid ACK is uncertain, exposes the retained key, and a later caller retry reuses bytes', async t => {
  const { api, state, calls, clock } = harness(t, { retryCount: 0 });
  state.hook = call => call.url.pathname === '/api/jobs' ? json({ id: 'incorrect' }) : null;
  const ticket = api.target('Kitchen').setContent('<p>Receipt</p>').setWidth(72);
  let key;
  await assert.rejects(ticket.print(), error => { key = error.details.idempotencyKey; return error.code === 'ACK_INVALID' && error.delivery === 'unknown'; });
  clock.now += 10001;
  await ticket.render(); // A new preview cannot replace an uncertain submission's bytes.
  state.hook = null;
  const result = await ticket.print(); assert.equal(result.idempotencyKey, key);
  const writes = calls.filter(c => c.url.pathname === '/api/jobs');
  assert.deepEqual(writes[0].init.body, writes[1].init.body);
});

for (const operation of ['receipt', 'beep', 'reprint']) test(`${operation} retains uncertainty when an automatic retry is rejected`, async t => {
  const { api, state, clock, calls } = harness(t);
  await api.connect();
  let submissions = 0;
  state.hook = call => {
    if (call.url.pathname !== '/api/jobs' && !call.url.pathname.endsWith('/reprints')) return;
    if (++submissions === 1) throw new TypeError('ACK lost after possible acceptance');
    return rejected('UNAUTHORIZED');
  };
  const options = { idempotencyKey: 'retained-intent' };
  const action = operation === 'receipt' ? api.target('Kitchen').setContent('<p>Receipt</p>').print(options)
    : operation === 'beep' ? api.target('Kitchen').beep(options) : api.reprintJob('original', options);
  const check = assert.rejects(action, error => error.code === 'UNAUTHORIZED' && error.delivery === 'unknown'
    && error.details.idempotencyKey === options.idempotencyKey);
  while (!submissions) await new Promise(resolve => setImmediate(resolve));
  await flush(); await clock.advance(0); await check;
  const writes = calls.filter(call => call.url.pathname === '/api/jobs' || call.url.pathname.endsWith('/reprints'));
  assert.equal(writes.length, 2); assert.deepEqual(writes[0].init.body, writes[1].init.body);
});

for (const firstOutcome of ['lost acknowledgement', 'accepted']) test(`later preview rejection cannot erase ${firstOutcome}`, async t => {
  const { api, state, calls, clock } = harness(t, { retryCount: 0 });
  const ticket = api.target('Kitchen').setContent('<p>Receipt</p>');
  if (firstOutcome === 'lost acknowledgement') {
    state.hook = call => { if (call.url.pathname === '/api/jobs') throw new TypeError('ACK lost'); };
    await assert.rejects(ticket.print({ idempotencyKey: 'retained' }), { delivery: 'unknown' });
  } else await ticket.print({ idempotencyKey: 'retained' });
  state.hook = call => call.url.pathname === '/api/jobs' ? rejected('RENDER_EXPIRED') : null;
  await assert.rejects(ticket.print({ idempotencyKey: 'retained' }), { code: 'RENDER_EXPIRED', delivery: 'unknown' });
  clock.now += 10001; await ticket.render();
  await assert.rejects(ticket.print({ idempotencyKey: 'retained' }), { code: 'RENDER_EXPIRED', delivery: 'unknown' });
  const writes = calls.filter(call => call.url.pathname === '/api/jobs');
  assert.equal(writes.length, 3);
  for (const write of writes) assert.deepEqual(write.init.body, writes[0].init.body);
});

test('disconnect before a later retry preserves the ticket original uncertainty', async t => {
  const { api, state } = harness(t, { retryCount: 0 });
  const ticket = api.target('Kitchen').setContent('<p>Receipt</p>');
  state.hook = call => { if (call.url.pathname === '/api/jobs') throw new TypeError('ACK lost'); };
  await assert.rejects(ticket.print({ idempotencyKey: 'retained' }), { delivery: 'unknown' });
  api.disconnect();
  await assert.rejects(ticket.print({ idempotencyKey: 'retained' }), { code: 'NOT_CONNECTED', delivery: 'unknown' });
});

for (const code of ['RENDER_EXPIRED', 'RENDER_CHANGED', 'PRINTER_SETTINGS_CHANGED']) test(`${code} requires a fresh explicit preview before printing again`, async t => {
  const { api, state, calls } = harness(t, { retryCount: 0 });
  const ticket = api.target('Kitchen').setContent('<p>Receipt</p>');
  const original = await ticket.render();
  state.hook = call => call.url.pathname === '/api/jobs' ? rejected(code) : null;
  await assert.rejects(ticket.print({ idempotencyKey: 'intent' }), { code, delivery: 'not_sent' });
  const requestCount = calls.length;
  await assert.rejects(ticket.print({ idempotencyKey: 'intent' }), { code });
  await assert.rejects(ticket.print({ idempotencyKey: 'other-key' }), { code });
  assert.equal(calls.length, requestCount); // No automatic re-render or repeat submission.
  state.hook = call => call.url.pathname === '/api/renders' ? rejected('RENDER_BUSY') : null;
  await assert.rejects(ticket.render(), { code: 'RENDER_BUSY' });
  await assert.rejects(ticket.print({ idempotencyKey: 'intent' }), { code });
  state.hook = null;
  const [first, second] = await Promise.all([ticket.render(), ticket.render()]);
  assert.notEqual(first.id, original.id); assert.equal(first.id, second.id);
  await ticket.print({ idempotencyKey: 'intent' });
  assert.deepEqual(calls.filter(call => call.body?.type === 'print').map(call => call.body.renderId), [original.id, first.id]);
});

for (const code of ['RENDER_CHANGED', 'PRINTER_SETTINGS_CHANGED']) test(`${code} cannot replace an earlier uncertain request`, async t => {
  const { api, state, calls } = harness(t, { retryCount: 0 });
  const ticket = api.target('Kitchen').setContent('<p>Receipt</p>');
  state.hook = call => { if (call.url.pathname === '/api/jobs') throw new TypeError('ACK lost'); };
  await assert.rejects(ticket.print({ idempotencyKey: 'intent' }), { delivery: 'unknown' });
  state.hook = call => call.url.pathname === '/api/jobs' ? rejected(code) : null;
  await assert.rejects(ticket.print({ idempotencyKey: 'intent' }), { code, delivery: 'unknown' });
  await ticket.render();
  await assert.rejects(ticket.print({ idempotencyKey: 'intent' }), { code, delivery: 'unknown' });
  const writes = calls.filter(call => call.body?.type === 'print');
  assert.equal(writes.length, 3); assert.ok(writes.every(call => digest(call.init.body) === digest(writes[0].init.body)));
  assert.equal(calls.filter(call => call.url.pathname === '/api/renders').length, 1);
});

test('a render started before rejection cannot clear the fresh-review requirement', async t => {
  const { api, state, calls, clock } = harness(t, { retryCount: 0 });
  const ticket = api.target('Kitchen').setContent('<p>Receipt</p>');
  const original = await ticket.render();
  let rejectPrint, finishRender;
  state.hook = call => {
    if (call.url.pathname === '/api/jobs') return new Promise(resolve => { rejectPrint = () => resolve(rejected('RENDER_CHANGED')); });
    if (call.url.pathname === '/api/renders') return new Promise(resolve => { finishRender = () => resolve(json({ ...original, id:'late-render',
      expiresAt:new Date(clock.now + 10000).toISOString() }, 200, { 'X-Entree-Content-SHA256':digest(call.init.body) })); });
  };
  const sending = ticket.print({ idempotencyKey:'intent' });
  const rejection = assert.rejects(sending, { code:'RENDER_CHANGED', delivery:'not_sent' });
  while (!rejectPrint) await new Promise(resolve => setImmediate(resolve));
  clock.now += 10001;
  const outdatedReview = ticket.render();
  while (!finishRender) await new Promise(resolve => setImmediate(resolve));
  rejectPrint(); await rejection;
  finishRender(); assert.equal((await outdatedReview).id,'late-render');
  const count = calls.length;
  await assert.rejects(ticket.print({idempotencyKey:'intent'}), {code:'RENDER_CHANGED'});
  assert.equal(calls.length,count);
  state.hook = null;
  const reviewed = await ticket.render();
  assert.notEqual(reviewed.id,'late-render');
  await ticket.print({idempotencyKey:'intent'});
});

test('expired reviewed preview is sent unchanged; print never silently prepares a different layout', async t => {
  const { api, state, clock, calls } = harness(t);
  const ticket = api.target('Kitchen').setContent('<p>Receipt</p>').setWidth(72);
  const preview = await ticket.render(); clock.now += 10001;
  state.hook = call => call.url.pathname === '/api/jobs' ? rejected('RENDER_EXPIRED') : null;
  await assert.rejects(ticket.print({ idempotencyKey: 'intent' }), { code: 'RENDER_EXPIRED' });
  assert.equal(calls.filter(c => c.url.pathname === '/api/renders').length, 1);
  assert.equal(calls.find(c => c.body?.type === 'print').body.renderId, preview.id);
  const revised = await ticket.render(); assert.notEqual(revised.id, preview.id);
  // The service proved non-acceptance. After explicit preparation/review, the same key can use the new preview.
  await assert.rejects(ticket.print({ idempotencyKey: 'intent' }), { code: 'RENDER_EXPIRED' });
  assert.deepEqual(calls.filter(c => c.body?.type === 'print').map(c => c.body.renderId), [preview.id, revised.id]);
});

test('helper methods use validated jobs and preserve raw bytes', async t => {
  const { api, calls } = harness(t);
  const printer = api.target('Kitchen');
  await printer.beep({ idempotencyKey: 'beep' });
  await printer.openDrawer({ idempotencyKey: 'drawer' });
  await printer.cut({ idempotencyKey: 'cut' });
  await printer.sendCommand(new Uint8Array([0, 27, 255]), { idempotencyKey: 'raw' });
  const writes = calls.filter(c => c.url.pathname === '/api/jobs');
  assert.deepEqual(writes.map(c => c.body.type), ['beep', 'open_cash_drawer', 'cut', 'send_command']);
  assert.equal(writes[3].body.bytesBase64, 'ABv/');
  assert.throws(() => printer.sendCommand('1B 42'), { code: 'COMMAND_INVALID' });
});

test('disconnect aborts an ignored transport, prevents late connection commit and requires reconnect', async t => {
  const { api, state, calls } = harness(t);
  let resolve;
  state.hook = () => new Promise(done => { resolve = done; });
  const attempt = api.connect(); await flush(); api.disconnect();
  await assert.rejects(attempt, { code: 'NOT_CONNECTED' });
  resolve(json({ serviceId: 'stale-host', bootId: 'stale', apiVersion: '0.0.1', printers: [{}] }));
  await flush();
  await assert.rejects(api.getPrinters(), { code: 'NOT_CONNECTED' });
  assert.equal(calls.length, 1);
  state.hook = null;
  assert.equal((await api.connect()).connection.serviceId, '127.0.0.1');
});

test('heartbeat does not overlap; missed replies degrade then offline and never submit printer commands', async t => {
  const { api, state, clock, calls } = harness(t, { heartbeat: { intervalMs: 100, timeoutMs: 20, missedLimit: 3 } });
  const states = []; api.subscribe(event => states.push(event.data.state), { events:['connection'] });
  await api.connect(); state.heartbeat = () => new Promise(() => {});
  await clock.advance(100); const count = calls.length;
  const p1 = api.resume(); const p2 = api.resume(); await flush();
  assert.equal(calls.length, count);
  await clock.advance(20); await Promise.all([p1, p2]);
  await clock.advance(240);
  assert.deepEqual(states, ['online', 'degraded', 'offline']);
  assert.equal(calls.filter(c => c.init.method === 'POST').length, 0);
  state.heartbeat = null;
  await api.resume();
  assert.deepEqual(states.slice(-2), ['reconnecting', 'online']);
  api.disconnect(); assert.equal(clock.timers.size, 0);
});

test('a reused address cannot change the service identity during heartbeat or explicit reconnect', async t => {
  const { api, state } = harness(t);
  await api.connect(); state.identity = 'different-installation';
  await api.resume();
  await assert.rejects(api.connect(), { code: 'SERVICE_MISMATCH' });
  assert.equal(state.jobs.size, 0);
});

test('schema and unsupported after-actions fail before any print request', async t => {
  const { api, calls } = harness(t);
  assert.throws(() => api.config({ port: '9779' }), { code: 'REQUEST_INVALID' });
  assert.throws(() => api.target('Kitchen').setContent([{ type: 'image', url: 'anywhere' }]), { code: 'CONTENT_TYPE_UNSUPPORTED' });
  await assert.rejects(api.target('Kitchen').setContent('receipt').print({ after: ['beep'] }), { code: 'REQUEST_INVALID' });
  await assert.rejects(api.target('Kitchen').setContent('receipt').print({ after: {openDrawer:true} }), { code: 'FIELD_UNSUPPORTED' });
  await assert.rejects(api.target('Kitchen').setContent('receipt').print({ after: {beep:1} }), { code: 'COMMAND_INVALID' });
  assert.equal(calls.length, 0);
});

test('trailing action options are frozen into the same receipt intent and concurrent retries', async t => {
  const {api,calls}=harness(t);await api.connect();
  const ticket=api.target('Kitchen').setContent('receipt'), after={beep:true,cut:true};
  const one=ticket.print({idempotencyKey:'with-actions',after});
  const two=ticket.print({idempotencyKey:'with-actions',after});
  after.beep=false;
  await Promise.all([one,two]);
  const sent=calls.filter(call=>call.body?.type==='print');
  assert.equal(sent.length,1);assert.deepEqual(sent[0].body.after,{cut:true,beep:true});
  await assert.rejects(ticket.print({idempotencyKey:'with-actions',after}),{code:'IDEMPOTENCY_CONFLICT'});
  await assert.rejects(api.target('Kitchen').beep({after:{beep:true}}),{code:'FIELD_UNSUPPORTED'});
});

test('repeated identical configurations share the handshake and one heartbeat timer', async t => {
  const { api, clock, calls } = harness(t, { heartbeat: { intervalMs: 100 } });
  const connections = Array.from({ length: 20 }, () => api.config({ ip: '127.0.0.1' }).connect());
  const results = await Promise.all(connections);
  assert.equal(results[0], results[1]);
  const printers = await results[0].getPrinters();
  printers[0].name = 'local edit';
  assert.equal((await results[1].getPrinters())[0].name, 'Kitchen');
  assert.equal(calls.filter(call => call.url.pathname === '/api/connection').length, 1);
  assert.equal(clock.timers.size, 1);
  await clock.advance(100);
  assert.equal(calls.filter(call => call.url.pathname === '/api/heartbeat').length, 1);
  api.disconnect(); assert.equal(clock.timers.size, 0);
  await api.connect(); await clock.advance(100);
  assert.equal(clock.timers.size, 1);
  assert.equal(calls.filter(call => call.url.pathname === '/api/heartbeat').length, 2);
});

test('verified aliases share monitoring while an old ticket retains its render and print intent', async t => {
  const { api, state, clock, calls, jobs } = harness(t, { heartbeat: { intervalMs: 100 } });
  state.identity = 'same-server';
  const events = []; api.subscribe(event => events.push(event.data), { events:['connection'] });
  const ticket = api.target('Kitchen').setContent('<p>Saved receipt</p>');
  const preview = await ticket.render();
  await api.connect({ ip: 'new-address', renderTimeoutMs: 20000, serviceId: 'same-server' });
  assert.equal(clock.timers.size, 1);
  await clock.advance(100);
  const heartbeats = calls.filter(call => call.url.pathname === '/api/heartbeat');
  assert.equal(heartbeats.length, 1);
  assert.equal(heartbeats[0].url.hostname, 'new-address');
  assert.equal(events.filter(event => event.state === 'online').length, 1);
  await ticket.print({ idempotencyKey: 'original-key' });
  const write = calls.find(call => call.url.pathname === '/api/jobs');
  assert.equal(write.url.hostname, 'new-address');
  assert.equal(write.body.renderId, preview.id);
  assert.equal(write.body.idempotencyKey, 'original-key');
  assert.equal(jobs.size, 1);
  assert.equal(calls.filter(call => call.url.pathname === '/api/renders').length, 1);
});

test('shared monitoring has one in-flight heartbeat and one missed-count transition across aliases', async t => {
  const { api, state, clock, calls } = harness(t, { heartbeat: { intervalMs: 100, timeoutMs: 20, missedLimit: 1 } });
  state.identity = 'same-server';
  await api.connect(); await api.connect({ ip: 'alias' });
  const events = []; api.subscribe(event => events.push(event.data), { events:['connection'] });
  state.heartbeat = () => new Promise(() => {});
  const first = api.resume(), second = api.resume(); await flush();
  assert.equal(calls.filter(call => call.url.pathname === '/api/heartbeat').length, 1);
  await clock.advance(20); await Promise.all([first, second]);
  assert.deepEqual(events.map(event => [event.state, event.missedCount]), [['offline', 1]]);
  assert.equal(calls.filter(call => call.init.method === 'POST').length, 0);
  assert.equal(clock.timers.size, 1);
  api.disconnect(); assert.equal(clock.timers.size, 0);
});

test('different credentials or heartbeat policies do not share a monitor', async t => {
  const { api, state, clock, calls } = harness(t, { heartbeat: { intervalMs: 100 } });
  state.identity = 'same-server';
  await api.connect();
  await api.connect({ token: 'another-test-token-'.repeat(3) });
  await api.connect({ heartbeat: { intervalMs: 200 } });
  assert.equal(clock.timers.size, 3);
  await api.resume();
  const heartbeats = calls.filter(call => call.url.pathname === '/api/heartbeat');
  assert.equal(heartbeats.length, 3);
  assert.equal(new Set(heartbeats.map(call => call.init.headers.Authorization)).size, 2);
  api.disconnect(); assert.equal(clock.timers.size, 0);
});

test('a late older handshake cannot restore the previous address after a newer verified connection', async t => {
  const { api, state, calls, clock } = harness(t, { heartbeat: { intervalMs: 100 } });
  state.identity = 'same-server';
  let release;
  state.hook = call => call.url.pathname === '/api/connection' && call.url.hostname === '127.0.0.1'
    ? new Promise(resolve => { release = resolve; }) : null;
  const original = api.connect(); await flush();
  const latest = await api.connect({ ip: 'new-address' });
  release(json({ serviceId: 'same-server', bootId: 'older-boot', apiVersion: '0.0.1', printers: [{ name: 'Kitchen' }] }));
  const older = await original;
  assert.equal(older.connection.ip, latest.connection.ip);
  assert.equal(older.connection.bootId, latest.connection.bootId);
  await clock.advance(100);
  const heartbeats = calls.filter(call => call.url.pathname === '/api/heartbeat');
  assert.equal(heartbeats.length, 1);
  assert.equal(heartbeats[0].url.hostname, 'new-address');
  assert.equal(clock.timers.size, 1);
});
