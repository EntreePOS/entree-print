import test from 'node:test';
import assert from 'node:assert/strict';
import { webcrypto } from 'node:crypto';
import { createEntreePrint } from '../entree-print.mjs';

import { Store } from './outbox-store.mjs';

const intent = (key = 'one', printer = 'cashier') => ({ idempotencyKey: key, serviceId: 'server', printer,
  content: '<p>欢迎 café</p>', metadata: { orderID: 'order-42', itemNames: ['饭'] } });
function harness(t, store = new Store()) {
  const calls = [], jobs = new Map(); let renders = 0;
  const faults = { loseAck: false, renderOffline: null, reject: null };
  const clients = [];
  function client() {
    const api = createEntreePrint({ token: 't'.repeat(32), retryCount: 0, heartbeat: { intervalMs: 60000 } }, {
      crypto: webcrypto, outboxStorage: store, fetch: async (url, options) => {
        const path = new URL(url).pathname, body = options.body && JSON.parse(new TextDecoder().decode(options.body));
        calls.push({ path, body, wire: options.body && Buffer.from(options.body).toString('hex') });
        const json = (value, status = 200) => new Response(JSON.stringify(value), { status,
          headers: { 'X-Entree-Content-SHA256': options.headers['X-Entree-Content-SHA256'] || '' } });
        if (path === '/api/connection') return json({ serviceId: 'server', bootId: 'boot', apiVersion: '0.0.1', printers: [{ name: 'cashier' }] });
        if (path === '/api/renders') {
          if (faults.renderOffline === body.printer) throw new TypeError('connection lost');
          return json({ id: `render-${++renders}`, serviceId: 'server', html: body.html, expiresAt: new Date(Date.now() + 60000).toISOString() });
        }
        assert.equal(path, '/api/jobs');
        const saved = store.records.find(record => record.idempotencyKey === body.idempotencyKey);
        assert.equal(saved.state, 'submitting');
        assert.equal(saved.wire.json, new TextDecoder().decode(options.body));
        if (faults.reject) return json({ error: { code: faults.reject, message: faults.reject, delivery: 'not_sent' } }, 410);
        const job = jobs.get(body.idempotencyKey) || { id: `job-${jobs.size}`, serviceId: 'server', idempotencyKey: body.idempotencyKey,
          state: 'accepted', integrity: { verified: true, requestDigest: options.headers['X-Entree-Content-SHA256'] } };
        jobs.set(body.idempotencyKey, job);
        if (faults.loseAck) throw new TypeError('lost acknowledgement');
        return json(job, 202);
      }
    });
    clients.push(api); return api;
  }
  t.after(() => clients.forEach(api => api.disconnect()));
  return { client, store, calls, jobs, faults };
}

test('outbox persists a snapshot before any network; identical enqueue shares the intent, changed content conflicts', async t => {
  const { client, calls, store } = harness(t); const outbox = client().outbox();
  const value = intent(); const pending = outbox.enqueue(value); value.content = 'changed'; value.metadata.itemNames[0] = 'changed';
  const saved = await pending;
  assert.equal(calls.length, 0); assert.equal(saved.state, 'pending');
  assert.equal(saved.content, intent().content); assert.equal(saved.metadata.itemNames[0], '饭');
  assert.equal((await outbox.enqueue(intent())).localId, saved.localId);
  await assert.rejects(outbox.enqueue({ ...intent(), printer: 'kitchen' }), { code: 'OUTBOX_CONFLICT' });
  assert.equal(store.records.length, 1);
  saved.content = 'mutated result'; assert.equal((await outbox.list())[0].content, intent().content);
});

test('a new SDK instance sends a previously unsent ticket and never repeats accepted local records', async t => {
  const { client, jobs, calls } = harness(t); const first = client();
  await first.outbox().enqueue(intent()); first.disconnect();
  const next = client(); const print = await next.connect();
  assert.equal((await next.outbox().flush(print))[0].state, 'accepted');
  const count = calls.length;
  assert.deepEqual(await next.outbox().flush(print), []);
  assert.equal(calls.length, count); assert.equal(jobs.size, 1);
});

test('outbox persists trailing actions and recovers their identical wire after lost acknowledgement',async t=>{
  const {client,store,faults,calls}=harness(t), first=client();
  await first.outbox().enqueue({...intent(),after:{beep:true}});
  assert.deepEqual(store.records[0].after,{beep:true});
  await assert.rejects(first.outbox().enqueue({...intent(),after:{cut:true}}),{code:'OUTBOX_CONFLICT'});
  faults.loseAck=true;await first.outbox().flush(await first.connect());first.disconnect();
  faults.loseAck=false;const next=client();await next.outbox().flush(await next.connect());
  const sent=calls.filter(call=>call.path==='/api/jobs');
  assert.equal(sent.length,2);assert.equal(sent[0].wire,sent[1].wire);
  assert.deepEqual(sent[0].body.after,{beep:true});
  assert.equal(calls.filter(call=>call.path==='/api/renders').length,1);
});

test('lost ACK recovery after SDK replacement resends exact saved bytes without another render', async t => {
  const { client, faults, calls, jobs } = harness(t); const api = client();
  await api.outbox().enqueue(intent()); faults.loseAck = true;
  assert.equal((await api.outbox().flush(await api.connect()))[0].state, 'uncertain');
  api.disconnect(); faults.loseAck = false;
  const next = client(); assert.equal((await next.outbox().flush(await next.connect()))[0].state, 'accepted');
  assert.equal(calls.filter(call => call.path === '/api/renders').length, 1);
  assert.equal(new Set(calls.filter(call => call.path === '/api/jobs').map(call => call.wire)).size, 1);
  assert.equal(jobs.size, 1);
});

for (const firstOutcome of ['lost acknowledgement', 'failed acceptance save']) test(`outbox preserves ${firstOutcome} across restart and later rejection`, async t => {
  const { client, faults, calls, jobs, store } = harness(t);
  const api = client(), selector = { serviceId: 'server', idempotencyKey: 'one' };
  await api.outbox().enqueue(intent()); await api.outbox().enqueue(intent('two'));
  const print = await api.connect();
  if (firstOutcome === 'lost acknowledgement') {
    faults.loseAck = true; await api.outbox().flush(print);
  } else {
    store.failState = 'accepted';
    await assert.rejects(api.outbox().flush(print), { code: 'OUTBOX_STORAGE_FAILED' });
  }
  assert.equal(store.records[0].mayHaveAccepted, true);
  const originalWire = structuredClone(store.records[0].wire);
  api.disconnect(); store.failState = null; faults.loseAck = false; faults.reject = 'RENDER_EXPIRED';
  const next = client(), connected = await next.connect();
  for (let attempt = 0; attempt < 2; attempt++) {
    if (attempt) await next.outbox().retry(selector);
    const [record] = await next.outbox().flush(connected);
    assert.equal(record.state, 'uncertain'); assert.equal(record.error.delivery, 'unknown');
    assert.equal(record.mayHaveAccepted, true); assert.deepEqual(record.wire, originalWire);
    assert.equal(store.records[1].state, 'pending');
  }
  assert.equal(jobs.size, 1); assert.equal(calls.filter(call => call.path === '/api/renders').length, 1);
  assert.equal(new Set(calls.filter(call => call.path === '/api/jobs').map(call => call.wire)).size, 1);
});

for (const state of ['add', 'prepared', 'submitting', 'accepted']) test(`storage failure at ${state} preserves recoverability`, async t => {
  const { client, store, jobs, calls } = harness(t); const api = client();
  store.failState = state;
  if (state === 'add') {
    await assert.rejects(api.outbox().enqueue(intent()), { code: 'OUTBOX_STORAGE_FAILED' });
    assert.equal(calls.length, 0); return;
  }
  await api.outbox().enqueue(intent());
  const print = await api.connect();
  await assert.rejects(api.outbox().flush(print), { code: 'OUTBOX_STORAGE_FAILED' });
  assert.equal(jobs.size, state === 'accepted' ? 1 : 0);
  store.failState = null; api.disconnect();
  const next = client(); await next.outbox().flush(await next.connect()); assert.equal(jobs.size, 1);
  if (state === 'accepted') assert.equal(calls.filter(call => call.path === '/api/renders').length, 1);
});

test('an offline destination preserves order while another printer proceeds', async t => {
  const { client, faults, jobs, store } = harness(t); const api = client();
  await api.outbox().enqueue(intent('k1', 'kitchen'));
  await api.outbox().enqueue(intent('k2', 'kitchen'));
  await api.outbox().enqueue(intent('c1'));
  faults.renderOffline = 'kitchen';
  const print = await api.connect(); await api.outbox().flush(print);
  assert.deepEqual([...jobs.keys()], ['c1']);
  assert.deepEqual(store.records.slice(0, 2).map(row => row.state), ['pending', 'pending']);
  faults.renderOffline = null; await api.outbox().flush(print);
  assert.deepEqual([...jobs.keys()], ['c1', 'k1', 'k2']);
});

test('two SDK instances sharing storage serialize dispatch for each destination', async t => {
  const { client, jobs, calls } = harness(t); const a = client(), b = client();
  await a.outbox().enqueue(intent()); await a.outbox().enqueue(intent('two'));
  const [pa, pb] = await Promise.all([a.connect(), b.connect()]);
  await Promise.all([a.outbox().flush(pa), b.outbox().flush(pb)]);
  assert.equal(jobs.size, 2); assert.equal(calls.filter(call => call.path === '/api/jobs').length, 2);
});

test('expired saved wire needs attention and never silently prepares a replacement', async t => {
  const { client, faults, calls, store } = harness(t); const api = client();
  await api.outbox().enqueue(intent()); faults.reject = 'RENDER_EXPIRED';
  const print = await api.connect(); await api.outbox().flush(print); const count = calls.length;
  await api.outbox().flush(print); assert.equal(calls.length, count);
  assert.equal(store.records[0].state, 'needs_attention'); assert.ok(store.records[0].wire);
});

test('wrong owning service is ignored and another SDK library cannot dispatch this outbox', async t => {
  const { client, calls } = harness(t); const a = client(), b = client();
  await a.outbox().enqueue({ ...intent(), serviceId: 'another-server' });
  await assert.rejects(a.outbox().flush(await b.connect()), { code: 'NOT_CONNECTED' });
  const print = await a.connect(); const count = calls.length;
  assert.deepEqual(await a.outbox().flush(print), []); assert.equal(calls.length, count);
});

test('corrupted source or saved request cannot be dispatched', async t => {
  const { client, store, faults, calls } = harness(t); const api = client();
  await api.outbox().enqueue(intent()); const print = await api.connect();
  store.records[0].content = 'corrupt';
  await assert.rejects(api.outbox().flush(print), { code: 'OUTBOX_CORRUPT' });
  assert.equal(calls.filter(call => call.path === '/api/jobs').length, 0);
  store.records[0].content = intent().content; faults.loseAck = true;
  await api.outbox().flush(print); faults.loseAck = false;
  store.records[0].wire.json += ' ';
  const count = calls.length;
  await assert.rejects(api.outbox().flush(print), { code: 'OUTBOX_CORRUPT' });
  assert.equal(calls.length, count);
});

test('an operator can cancel an unsent intent, but cannot cancel a possible submission locally', async t => {
  const { client, jobs, faults } = harness(t); const api = client(); const outbox = api.outbox();
  await outbox.enqueue(intent()); await outbox.enqueue(intent('two'));
  const selector = { serviceId: 'server', idempotencyKey: 'one' };
  assert.equal((await outbox.cancel(selector)).state, 'cancelled');
  await assert.rejects(outbox.retry(selector), { code: 'OUTBOX_TERMINAL' });
  faults.loseAck = true; const print = await api.connect(); await outbox.flush(print);
  assert.deepEqual([...jobs.keys()], ['two']);
  await assert.rejects(outbox.cancel({ ...selector, idempotencyKey: 'two' }), { code: 'OUTBOX_MAY_HAVE_PRINTED' });
});

test('explicit retry preserves saved wire and never changes the intent key', async t => {
  const { client, faults, calls } = harness(t); const api = client(); const outbox = api.outbox();
  await outbox.enqueue(intent()); faults.reject = 'RENDER_EXPIRED';
  const print = await api.connect(); await outbox.flush(print);
  const before = (await outbox.list())[0].wire;
  faults.reject = null; await outbox.retry({ serviceId: 'server', idempotencyKey: 'one' }); await outbox.flush(print);
  const after = (await outbox.list())[0]; assert.equal(after.state, 'accepted'); assert.deepEqual(after.wire, before);
  assert.equal(calls.filter(call => call.path === '/api/renders').length, 1);
});

test('explicitly stopping retries releases later work without deleting uncertain evidence or sending a cancellation', async t => {
  const { client, faults, calls, jobs } = harness(t); const api = client(); const outbox = api.outbox();
  await outbox.enqueue(intent()); await outbox.enqueue(intent('two'));
  faults.loseAck = true; const print = await api.connect(); await outbox.flush(print);
  const original = (await outbox.list())[0], count = calls.length;
  const stopped = await outbox.stopRetrying({ serviceId: 'server', idempotencyKey: 'one' });
  assert.equal(stopped.state, 'stopped'); assert.deepEqual(stopped.wire, original.wire);
  assert.deepEqual(stopped.error, original.error); assert.equal(calls.length, count);
  assert.equal((await outbox.enqueue(intent())).state, 'stopped');
  faults.loseAck = false; await outbox.flush(print);
  assert.deepEqual([...jobs.keys()], ['one', 'two']);
  assert.equal(calls.filter(call => call.path === '/api/jobs' && call.body.idempotencyKey === 'one').length, 1);
});
