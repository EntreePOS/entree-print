import test from 'node:test';
import assert from 'node:assert/strict';
import { webcrypto, createHash } from 'node:crypto';
import { createEntreePrint } from '../entree-print.mjs';
import { Clock, flush } from './helpers.mjs';

const response = (body, status = 200, headers = {}) => new Response(JSON.stringify(body), { status, headers });
const token = 'test-discovery-token-'.repeat(3);

function setup(t) {
  const clock = new Clock();
  const state = { nodes: new Map(), calls: [], scans: [], autoCandidates: [] };
  const api = createEntreePrint({ ip: 'old-host', token, retryCount: 0,
    heartbeat: { intervalMs: 100, timeoutMs: 20, missedLimit: 3 } }, {
    crypto: webcrypto, now: () => clock.now, setTimeout: clock.setTimeout, clearTimeout: clock.clearTimeout,
    subscribeResume(handler) { state.wake = handler; state.resumeListeners = (state.resumeListeners ?? 0) + 1;
      return () => { state.resumeListeners--; state.wake = null; }; },
    discover: options => new Promise(resolve => {
      state.scans.push({ ...options, done: resolve });
      options.signal.addEventListener('abort', resolve, { once: true });
      for (const candidate of state.autoCandidates) options.onCandidate(candidate);
    }),
    fetch: async (url, init) => {
      const parsed = new URL(url); const node = state.nodes.get(parsed.hostname);
      state.calls.push({ url: parsed, init });
      if (!node || node.down) throw new TypeError('unreachable');
      if (node.hang) return new Promise(() => {});
      if (parsed.pathname === '/api/health') {
        assert.equal(init.headers.Authorization, undefined, 'health discovery must not forward credentials');
        return response({ service: 'entree-print-plugin', serviceId: node.id, bootId: 'boot', apiVersion: node.version ?? '0.0.1',
          requestId: node.staleNonce ? 'old-nonce' : parsed.searchParams.get('requestId') });
      }
      assert.equal(init.headers.Authorization, `Bearer ${token}`);
      if (init.headers['X-Entree-Service-ID'] && init.headers['X-Entree-Service-ID'] !== node.id)
        return response({ error: { code: 'SERVICE_MISMATCH', delivery: 'not_sent' } }, 409);
      if (parsed.pathname === '/api/connection') {
        if (node.denied) return response({ error: { code: 'UNAUTHORIZED', delivery: 'not_sent' } }, 401);
        return response({ serviceId: node.id, bootId: 'boot', apiVersion: '0.0.1', printers: node.empty ? [] : [{ name: 'Kitchen' }] });
      }
      if (parsed.pathname === '/api/heartbeat') return response({ serviceId: node.id, bootId: 'boot', requestId: parsed.searchParams.get('requestId') });
      if (parsed.pathname === '/api/printers') return response([{ name: 'Kitchen' }]);
      const body = JSON.parse(new TextDecoder().decode(init.body));
      const digest = createHash('sha256').update(init.body).digest('hex');
      if (parsed.pathname === '/api/renders') return response({ serviceId: node.id, id: 'prepared', html: '<html>original</html>',
        expiresAt: new Date(clock.now + 30000).toISOString() }, 200, { 'X-Entree-Content-SHA256': digest });
      if (parsed.pathname === '/api/jobs') return response({ serviceId: node.id, id: 'one-job', idempotencyKey: body.idempotencyKey,
        integrity: { verified: true } }, 202, { 'X-Entree-Content-SHA256': digest });
      throw new Error('Unexpected request');
    }
  });
  t.after(() => api.disconnect());
  return { api, state, clock };
}

test('HTTPS discovery never probes a plaintext announcement', async t => {
  const {api,state} = setup(t);
  api.config({protocol:'https'});
  state.nodes.set('plain',{id:'same'}); state.nodes.set('secure',{id:'same'});
  const connecting=api.search({timeoutMs:500}).connect(); await flush();
  state.scans[0].onCandidate({ip:'plain',port:9779,protocol:'http',serviceId:'same'});
  state.scans[0].onCandidate({ip:'secure',port:9779,protocol:'https',serviceId:'same'});
  const library=await connecting;
  assert.equal(library.connection.protocol,'https');
  assert.ok(state.calls.every(call=>call.url.protocol==='https:' && call.url.hostname==='secure'));
});

test('HTTP and HTTPS libraries never share or replace one another’s connection monitor', async t => {
  const {api,state,clock} = setup(t);
  state.nodes.set('same-host',{id:'same'});
  const secure=await api.connect({ip:'same-host',protocol:'https'});
  const plain=await api.connect({ip:'same-host',protocol:'http'});
  await secure.getPrinters(); assert.equal(state.calls.at(-1).url.protocol,'https:');
  await plain.getPrinters(); assert.equal(state.calls.at(-1).url.protocol,'http:');
  assert.equal(secure.connection.protocol,'https'); assert.equal(plain.connection.protocol,'http');
  await clock.advance(100);
  assert.deepEqual(new Set(state.calls.filter(call=>call.url.pathname==='/api/heartbeat').map(call=>call.url.protocol)),new Set(['https:','http:']));
});

test('search is lazy, shared when awaited, deduplicates identity, and closes at its deadline', async t => {
  const { api, state, clock } = setup(t);
  state.nodes.set('host-a', { id: 'a' }); state.nodes.set('host-alias', { id: 'a' }); state.nodes.set('host-b', { id: 'b' });
  const builder = api.search({ timeoutMs: 500 });
  assert.equal(state.scans.length, 0);
  const first = Promise.resolve(builder); const second = Promise.resolve(builder); await flush();
  assert.equal(state.scans.length, 1);
  for (const ip of ['host-a', 'host-alias', 'host-b']) state.scans[0].onCandidate({ ip, port: 9779 });
  await flush(); await clock.advance(500);
  const a = await first; const b = await second;
  assert.equal(a.length, 2); assert.deepEqual(a.map(record => record.serviceId), ['a', 'b']);
  a[0].ip = 'changed'; assert.notEqual(b[0].ip, 'changed');
  assert.equal(state.scans[0].signal.aborted, true);
  assert.equal(state.calls.filter(call => call.url.pathname === '/api/connection').length, 0);
  assert.equal(clock.timers.size, 0);
});

test('search().connect() returns first successful handshake without waiting for slower services or window end', async t => {
  const { api, state, clock } = setup(t);
  state.nodes.set('slow', { id: 'slow', hang: true }); state.nodes.set('denied', { id: 'denied', denied: true }); state.nodes.set('ready', { id: 'ready' });
  const builder = api.search({ timeoutMs: 500 }); const connected = builder.connect(); await flush();
  for (const ip of ['slow', 'denied', 'ready']) state.scans[0].onCandidate({ ip, port: 9779 });
  await flush();
  const print = await connected;
  assert.equal(print.connection.serviceId, 'ready');
  assert.equal((await print.getPrinters())[0].name, 'Kitchen');
  assert.equal(clock.now, 1000000); // No deadline advance was needed.
  await print.target('Kitchen').beep({ idempotencyKey: 'helper' });
  assert.equal(state.calls.find(call => call.init.method === 'POST').url.hostname, 'ready');
  state.nodes.set('other', { id: 'other' });
  await api.connect({ ip: 'other' });
  assert.equal(await builder.connect(), print);
  await print.getPrinters();
  assert.equal(state.calls.at(-1).url.hostname, 'ready');
  await clock.advance(500);
  assert.equal(state.scans[0].signal.aborted, true);
  assert.deepEqual((await builder).map(item => item.serviceId), ['denied', 'ready']);
});

test('await search then connect reuses discovered addresses without another UDP scan', async t => {
  const { api, state, clock } = setup(t);
  state.nodes.set('ready', { id: 'ready' });
  const builder = api.search({ timeoutMs: 100 }); const list = Promise.resolve(builder); await flush();
  state.scans[0].onCandidate({ ip: 'ready', port: 9779 }); await flush(); await clock.advance(100); await list;
  assert.equal((await builder.connect()).connection.serviceId, 'ready');
  assert.equal(state.scans.length, 1);
});

test('empty search returns [] but connect throws; incompatible, stale and mismatched identities are excluded', async t => {
  const { api, state, clock } = setup(t);
  const empty = api.search({ timeoutMs: 100 }); const collected = Promise.resolve(empty); await flush();
  await clock.advance(100); assert.deepEqual(await collected, []);
  await assert.rejects(empty.connect(), { code: 'SERVICE_NOT_FOUND' });
  state.nodes.set('old', { id: 'old', version: '9.0.0' }); state.nodes.set('stale', { id: 'stale', staleNonce: true }); state.nodes.set('wrong', { id: 'wrong' });
  const scan = api.search({ timeoutMs: 100 }); const result = Promise.resolve(scan); await flush();
  state.scans[1].onCandidate({ ip: 'old', port: 9779 }); state.scans[1].onCandidate({ ip: 'stale', port: 9779 });
  state.scans[1].onCandidate({ ip: 'wrong', port: 9779, serviceId: 'advertised-other' });
  await flush(); await clock.advance(100);
  assert.deepEqual(await result, []);
  assert.equal(state.calls.filter(call => call.url.pathname === '/api/connection').length, 0);
});

test('failed authorized handshakes report per-candidate errors', async t => {
  const { api, state, clock } = setup(t);
  state.nodes.set('denied', { id: 'denied', denied: true }); state.nodes.set('empty', { id: 'empty', empty: true });
  const pending = api.search({ timeoutMs: 100 }).connect(); pending.catch(() => {}); await flush();
  for (const ip of ['denied', 'empty']) state.scans[0].onCandidate({ ip, port: 9779 });
  await flush(); await clock.advance(100);
  await assert.rejects(pending, error => error.code === 'CONNECTION_FAILED' &&
    error.details.candidates.some(candidate => candidate.code === 'UNAUTHORIZED') && error.details.candidates.some(candidate => candidate.code === 'NO_PRINTERS'));
});

test('late discovery cannot overwrite a newer explicit service selection', async t => {
  const { api, state } = setup(t);
  state.nodes.set('found', { id: 'found' }); state.nodes.set('chosen', { id: 'chosen' });
  const older = api.search().connect(); older.catch(() => {}); await flush();
  await api.config({ ip: 'chosen' }).connect();
  state.scans[0].onCandidate({ ip: 'found', port: 9779 }); await flush();
  await assert.rejects(older, { code: 'CONNECTION_SUPERSEDED' });
  await api.target('Kitchen').beep({ idempotencyKey: 'selection' });
  assert.equal(state.calls.find(call => call.init.method === 'POST').url.hostname, 'chosen');
});

test('disconnect cancels discovery and ignores callbacks that arrive after cancellation', async t => {
  const { api, state, clock } = setup(t);
  const pending = api.search().connect(); pending.catch(() => {}); await flush();
  api.disconnect();
  await assert.rejects(pending, { code: 'NOT_CONNECTED' });
  state.scans[0].onCandidate({ ip: 'late', port: 9779 }); await flush();
  assert.equal(state.calls.length, 0); assert.equal(clock.timers.size, 0);
});

test('candidate floods have bounded HTTP concurrency and deadline cancellation even if transport ignores abort', async t => {
  const { api, state, clock } = setup(t);
  const pending = api.search({ timeoutMs: 100 }).connect(); pending.catch(() => {}); await flush();
  for (let index = 0; index < 100; index++) {
    const ip = `host-${index}`; state.nodes.set(ip, { id: ip, hang: true });
    state.scans[0].onCandidate({ ip, port: 9779 });
  }
  await flush(); assert.equal(state.calls.length, 4);
  await clock.advance(100);
  await assert.rejects(pending, { code: 'CONNECTION_FAILED' });
  assert.ok(state.calls.every(call => call.init.signal.aborted)); assert.equal(clock.timers.size, 0);
});

test('disconnect before the deferred discovery start performs no transport I/O', async t => {
  const { api, state } = setup(t);
  const pending = api.search().connect(); pending.catch(() => {}); api.disconnect();
  await assert.rejects(pending, { code: 'NOT_CONNECTED' });
  await flush(); assert.equal(state.scans.length, 0);
});

test('failed address recovery backs off, can be cancelled, and creates no printer commands', async t => {
  const { api, state, clock } = setup(t);
  state.nodes.set('old-host', { id: 'original' }); await api.connect(); state.nodes.get('old-host').down = true;
  await api.resume(); await api.resume(); await api.resume(); await clock.advance(1000);
  assert.equal(state.scans.length, 1);
  // End the scan early with no candidates, then check that no second scan starts immediately.
  state.scans[0].done(); await flush(); await clock.advance(1000);
  assert.equal(state.scans.length, 1);
  api.disconnect(); assert.equal(clock.timers.size, 0);
  assert.equal(state.calls.filter(call => call.init.method === 'POST').length, 0);
});

test('ordinary browser without native adapter reports discovery unavailable without network scanning', async () => {
  let calls = 0;
  const api = createEntreePrint({ token }, { fetch: () => { calls++; }, crypto: webcrypto });
  await assert.rejects(Promise.resolve(api.search()), { code: 'DISCOVERY_UNAVAILABLE' });
  assert.equal(calls, 0); api.disconnect();
});

test('resume observation marks stale, refreshes inventory, and removes lifecycle listeners on disconnect', async t => {
  const { api, state } = setup(t);
  const events = []; api.subscribe(event => events.push(event.data), { events:['connection'] });
  state.nodes.set('old-host', { id: 'original' }); await api.connect();
  assert.equal(state.resumeListeners, 1);
  state.wake(); state.wake(); await flush();
  assert.equal(state.calls.filter(call => call.url.pathname === '/api/heartbeat').length, 1);
  assert.equal(state.calls.filter(call => call.url.pathname === '/api/connection').length, 2);
  assert.deepEqual(events.slice(-3).map(event => event.state), ['degraded', 'reconnecting', 'online']);
  assert.equal(events.at(-3).printersStale, true); assert.equal(events.at(-1).printersStale, false);
  api.disconnect(); assert.equal(state.resumeListeners, 0);
});

test('automatic address recovery stays on the original identity and moves already prepared tickets with it', async t => {
  const { api, state, clock } = setup(t);
  state.nodes.set('old-host', { id: 'original' });
  await api.connect();
  const ticket = api.target('Kitchen').setContent('<p>Original</p>').setWidth(72);
  await ticket.render();
  state.nodes.set('old-host', { id: 'other-store' });
  state.nodes.set('new-host', { id: 'original' }); state.nodes.set('unrelated', { id: 'unrelated' });
  state.autoCandidates = [{ ip: 'unrelated', port: 9779, serviceId: 'unrelated' }, { ip: 'new-host', port: 9779, serviceId: 'original' }];
  await api.resume(); await api.resume(); await api.resume();
  await clock.advance(1000);
  assert.equal(state.scans[0].serviceId, 'original');
  assert.equal(state.calls.some(call => call.url.hostname === 'unrelated'), false);
  assert.equal(state.calls.filter(call => call.init.method === 'POST').length, 1); // Only the earlier preview.
  await ticket.print({ idempotencyKey: 'same-intent' });
  const print = state.calls.find(call => call.url.pathname === '/api/jobs');
  assert.equal(print.url.hostname, 'new-host');
  assert.equal(JSON.parse(new TextDecoder().decode(print.init.body)).renderId, 'prepared');
  assert.equal(state.calls.filter(call => call.url.pathname === '/api/renders').length, 1);
});

test('one recovery scan updates every verified alias without replaying either prepared ticket', async t => {
  const { api, state, clock } = setup(t);
  state.nodes.set('old-host', { id: 'original' }); state.nodes.set('alias', { id: 'original' });
  await api.connect();
  const first = api.target('Kitchen').setContent('<p>First</p>'); await first.render();
  await api.connect({ ip: 'alias', renderTimeoutMs: 20000 });
  const second = api.target('Kitchen').setContent('<p>Second</p>'); await second.render();
  assert.equal(clock.timers.size, 1); assert.equal(state.resumeListeners, 1);
  state.nodes.set('old-host', { id: 'replacement' }); state.nodes.get('alias').down = true;
  state.nodes.set('new-host', { id: 'original' });
  state.autoCandidates = [{ ip: 'new-host', port: 9779, serviceId: 'original' }];
  await api.resume(); await api.resume(); await api.resume(); await clock.advance(1000);
  assert.equal(state.scans.length, 1);
  assert.equal(state.calls.filter(call => call.init.method === 'POST').length, 2); // Original previews only.
  await first.print({ idempotencyKey: 'first-key' }); await second.print({ idempotencyKey: 'second-key' });
  const writes = state.calls.filter(call => call.url.pathname === '/api/jobs');
  assert.deepEqual(writes.map(call => call.url.hostname), ['new-host', 'new-host']);
  assert.deepEqual(writes.map(call => JSON.parse(new TextDecoder().decode(call.init.body)).idempotencyKey), ['first-key', 'second-key']);
  assert.equal(state.calls.filter(call => call.url.pathname === '/api/renders').length, 2);
  assert.equal(clock.timers.size, 1);
  api.disconnect(); assert.equal(clock.timers.size, 0); assert.equal(state.resumeListeners, 0);
});
