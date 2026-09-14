import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, webcrypto } from 'node:crypto';
import { createEntreePrint } from '../entree-print.mjs';
import { Clock, flush } from './helpers.mjs';
import { Store } from './outbox-store.mjs';

const json = (body, status = 200, headers = {}) => new Response(JSON.stringify(body), { status, headers });
const node = name => ({ ip:name, serviceId:name, port:9779, token:`${name}-credential-`.repeat(4) });
const nodes = () => [node('primary'), node('backup')];
function harness(t, outboxStorage) {
  const clock = new Clock(), calls = [], jobs = new Map();
  const state = { primary:{}, backup:{} };
  const api = createEntreePrint({retryCount:0}, {crypto:webcrypto, outboxStorage, now:() => clock.now,
    setTimeout:clock.setTimeout, clearTimeout:clock.clearTimeout,
    fetch:async (url, init) => {
      const address = new URL(url), name = address.hostname, server = state[name] ??= {};
      const body = init.body && JSON.parse(new TextDecoder().decode(init.body));
      calls.push({name,path:address.pathname,body,signal:init.signal});
      assert.equal(init.headers.Authorization, `Bearer ${node(name).token}`);
      if (server.offline) throw new TypeError('Network down');
      if (address.pathname === '/api/connection') {
        if (server.pending) return server.pending;
        return json({serviceId:server.identity ?? name, bootId:'boot', apiVersion:'0.0.1', printers:server.empty ? [] :
          [{name:'Kitchen',connection:{type:'network',host:'192.168.1.80'}},{name:'cashier',connection:{type:'usb',host:null}}]});
      }
      if (address.pathname === '/api/printers') return json([{name:'Kitchen'},{name:'cashier'}]);
      if (address.pathname === '/api/heartbeat') return json({serviceId:name,bootId:'boot',requestId:address.searchParams.get('requestId')});
      if (address.pathname === '/api/jobs/lookup') return json(jobs.get(name + ':' + address.searchParams.get('idempotencyKey')));
      const digest = createHash('sha256').update(init.body).digest('hex');
      assert.equal(init.headers['X-Entree-Content-SHA256'],digest);
      assert.equal(init.headers['X-Entree-Service-ID'],name);
      const headers = {'X-Entree-Content-SHA256':digest};
      if (address.pathname === '/api/renders') return json({serviceId:name,id:name+'-render',html:'<p>厨房</p>',
        expiresAt:new Date(clock.now+3600000).toISOString()},200,headers);
      if (address.pathname === '/api/jobs') {
        const key = name+':'+body.idempotencyKey;
        if (body.type === 'print') assert.equal(body.renderId,name+'-render');
        if (!jobs.has(key)) jobs.set(key,{id:key,serviceId:name,idempotencyKey:body.idempotencyKey,state:'accepted',
          integrity:{verified:true,requestDigest:digest}});
        if (server.loseAck) throw new TypeError('Acknowledgement lost after acceptance');
        return json(jobs.get(key),202,headers);
      }
      throw new Error('Unexpected request');
    } });
  t.after(() => api.disconnect());
  return {api,clock,calls,state,jobs};
}

test('configured connection selects the primary without contacting unused nodes', async t => {
  const {api,calls} = harness(t);
  const print = await api.connect({nodes:nodes()});
  assert.equal(print.connection.serviceId,'primary');
  assert.equal(print.connection.printers,undefined);
  assert.deepEqual(print.connection.nodeSelection.nodes.map(item => item.state),['selected','not_checked']);
  assert.deepEqual(calls.map(item => item.name),['primary']);
  assert.deepEqual(await print.getPrinters(),[{name:'Kitchen'},{name:'cashier'}]);
});

test('connection falls back with each node credential and returns isolated diagnostic snapshots', async t => {
  const {api,state,calls} = harness(t); state.primary.offline = true;
  const candidates = nodes();
  const pending = api.connect({nodes:candidates});
  candidates[1].ip = 'mutated'; candidates[1].token = 'mutated';
  const print = await pending;
  assert.equal(print.connection.serviceId,'backup');
  assert.equal(print.connection.nodeSelection.usedBackup,true);
  assert.equal(print.connection.nodeSelection.nodes[0].code,'SERVICE_UNAVAILABLE');
  const snapshot = print.connection; snapshot.nodeSelection.nodes[0].ip = 'mutated';
  assert.equal(print.connection.nodeSelection.nodes[0].ip,'primary');
  assert.doesNotMatch(JSON.stringify(print.connection),/credential|token/);
  assert.equal(calls.filter(item => item.body).length,0);
});

test('a silent primary leaves time for backup selection within the total deadline', async t => {
  const {api,state,clock,calls} = harness(t); state.primary.pending = new Promise(() => {});
  const pending = api.connect({nodes:nodes(),timeoutMs:1000});
  await flush(); await clock.advance(500);
  assert.equal((await pending).connection.serviceId,'backup');
  assert.equal(calls[0].signal.aborted,true);
  assert.equal(clock.timers.size,1); // Only the selected service heartbeat remains.
});

test('all silent nodes finish with a bounded, credential-free failure', async t => {
  const {api,state,clock} = harness(t);
  state.primary.pending = state.backup.pending = new Promise(() => {});
  const rejected = assert.rejects(api.connect({nodes:nodes(),timeoutMs:1000}), error => {
    assert.equal(error.code,'SERVICE_UNAVAILABLE'); assert.equal(error.delivery,'not_sent');
    assert.doesNotMatch(JSON.stringify(error.details),/credential|token/); return true;
  });
  await clock.advance(1000); await rejected;
  assert.equal(clock.timers.size,0);
});

test('failover false tries only the first configured server', async t => {
  const {api,state,calls} = harness(t); state.primary.offline = true;
  await assert.rejects(api.connect({nodes:nodes(),failover:false}),{code:'SERVICE_UNAVAILABLE'});
  assert.deepEqual(calls.map(item => item.name),['primary']);
});

test('wrong identity and missing printers do not become the selected server', async t => {
  for (const property of [{identity:'unexpected'},{empty:true}]) {
    const {api,state} = harness(t); Object.assign(state.primary,property);
    const print = await api.connect({nodes:nodes()});
    assert.equal(print.connection.serviceId,'backup');
    assert.equal(print.connection.nodeSelection.nodes[0].code,property.empty ? 'NO_PRINTERS' : 'SERVICE_MISMATCH');
    api.disconnect();
  }
});

test('all node options are validated before contacting any candidate', t => {
  const {api,calls} = harness(t);
  for (const options of [{nodes:[]},{nodes:Array.from({length:9},() => node('primary'))},
    {nodes:[node('primary'),node('primary')]},{nodes:[node('primary'),{...node('backup'),ip:'primary'}]},
    {nodes:[node('primary'),{ip:'backup',serviceId:'backup'}]}, {nodes:[{...node('primary'),serviceId:''}]},
    {nodes:nodes(),failover:'yes'}, {nodes:nodes(),timeoutMs:0}, {nodes:nodes(),ip:'other'},
    {nodes:[{...node('primary'),printers:{cashier:'cashier'}}]}]) assert.throws(() => api.connect(options));
  assert.equal(calls.length,0);
});

test('disconnect prevents a pending selection from connecting the backup', async t => {
  const {api,state,calls,clock} = harness(t); state.primary.pending = new Promise(() => {});
  const rejected = assert.rejects(api.connect({nodes:nodes()}),{code:'CONNECTION_SUPERSEDED'});
  await flush(); api.disconnect(); await rejected;
  assert.deepEqual(calls.map(item => item.name),['primary']); assert.equal(clock.timers.size,0);
});

test('a newer direct connection wins over a delayed configured selection', async t => {
  const {api,state} = harness(t); let respond;
  state.primary.pending = new Promise(resolve => { respond = resolve; });
  const rejected = assert.rejects(api.connect({nodes:nodes()}),{code:'CONNECTION_SUPERSEDED'});
  await flush(); await api.connect(node('backup'));
  respond(json({serviceId:'primary',bootId:'boot',apiVersion:'0.0.1',printers:[{name:'Kitchen'}]}));
  await rejected;
  const job = await api.target('Kitchen').setContent('<p>New work</p>').print({idempotencyKey:'new'});
  assert.equal(job.serviceId,'backup');
});

test('backup connection cannot move USB receipts or device commands; explicit owner connection can', async t => {
  const {api,state,calls} = harness(t); state.primary.offline = true;
  const print = await api.connect({nodes:nodes()});
  await assert.rejects(print.target('cashier').setContent('<p>USB receipt</p>').print(),{code:'PRINTER_OWNER_REQUIRED'});
  await assert.rejects(print.target('unknown').setContent('<p>Unknown queue</p>').print(),{code:'PRINTER_OWNER_REQUIRED'});
  for (const method of ['beep','openDrawer','cut']) await assert.rejects(print.target('Kitchen')[method](),{code:'DEVICE_OWNER_REQUIRED'});
  await assert.rejects(print.target('Kitchen').sendCommand(new Uint8Array([27,64])),{code:'DEVICE_OWNER_REQUIRED'});
  assert.equal(calls.filter(item => item.body).length,0);
  const direct = await api.connect(node('backup'));
  assert.equal(direct.connection.nodeSelection,undefined);
  assert.equal((await direct.target('cashier').openDrawer({idempotencyKey:'explicit-device'})).serviceId,'backup');
});

test('lost acknowledgement stays on its original ticket owner after selecting a backup', async t => {
  const {api,state,jobs,calls} = harness(t);
  const primary = await api.connect({nodes:nodes()});
  const ticket = primary.target('Kitchen').setContent('<p>Original</p>');
  state.primary.loseAck = true;
  await assert.rejects(ticket.print({idempotencyKey:'original'}),{delivery:'unknown'});
  assert.equal(jobs.size,1);
  state.primary.offline = true;
  const backup = await api.connect({nodes:nodes()});
  await assert.rejects(ticket.print({idempotencyKey:'original'}),{delivery:'unknown'});
  assert.equal(calls.filter(item => item.name === 'backup' && item.body).length,0);
  assert.equal((await backup.target('Kitchen').setContent('<p>New</p>').print({idempotencyKey:'new'})).serviceId,'backup');
  state.primary.offline = state.primary.loseAck = false;
  assert.equal((await ticket.print({idempotencyKey:'original'})).serviceId,'primary');
  assert.equal((await primary.getJob({idempotencyKey:'original'})).serviceId,'primary');
  assert.equal(jobs.size,2);
});

test('disconnect inside the selected connection event cannot return a live library', async t => {
  const {api,clock} = harness(t);
  api.subscribe(event => { if (event.data.state === 'online') api.disconnect(); }, { events:['connection'] });
  await assert.rejects(api.connect({nodes:nodes()}), {code:'CONNECTION_SUPERSEDED'});
  assert.equal(clock.timers.size,0);
});

test('selected backup requires an explicit owner connection for reprints', async t => {
  const {api,state,calls} = harness(t); state.primary.offline = true;
  const print = await api.connect({nodes:nodes()});
  await assert.rejects(print.reprintJob('original',{idempotencyKey:'copy'}),{code:'JOB_OWNER_REQUIRED'});
  assert.equal(calls.filter(item => item.body).length,0);
});

test('backup outbox blocks USB preparation while allowing its network receipts', async t => {
  const store = new Store();
  const {api,state,calls,jobs} = harness(t,store); state.primary.offline = true;
  const outbox = api.outbox();
  for (const printer of ['cashier','Kitchen']) await outbox.enqueue({serviceId:'backup',printer,
    idempotencyKey:printer,content:'<p>Saved receipt</p>'});
  const results = await outbox.flush(await api.connect({nodes:nodes()}));
  assert.equal(results.find(item => item.printer === 'cashier').error.code,'PRINTER_OWNER_REQUIRED');
  assert.equal(results.find(item => item.printer === 'Kitchen').state,'accepted');
  assert.equal(calls.filter(item => item.body?.printer === 'cashier').length,0);
  assert.equal(jobs.size,1);
});

test('saved USB wire cannot bypass backup restrictions and remains recoverable by its owner', async t => {
  const store = new Store();
  const {api,state,calls,jobs} = harness(t,store);
  const outbox = api.outbox();
  await outbox.enqueue({serviceId:'backup',printer:'cashier',idempotencyKey:'saved',content:'<p>Saved</p>'});
  store.failState = 'submitting';
  await assert.rejects(outbox.flush(await api.connect(node('backup'))),{code:'OUTBOX_STORAGE_FAILED'});
  assert.equal(store.records[0].state,'prepared');
  const wire = structuredClone(store.records[0].wire);
  store.failState = null; state.primary.offline = true;
  const result = (await outbox.flush(await api.connect({nodes:nodes()})))[0];
  assert.equal(result.error.code,'PRINTER_OWNER_REQUIRED');
  assert.deepEqual(result.wire,wire);
  assert.equal(calls.filter(item => item.path === '/api/jobs').length,0);
  await outbox.retry({serviceId:'backup',idempotencyKey:'saved'});
  assert.equal((await outbox.flush(await api.connect(node('backup'))))[0].state,'accepted');
  assert.equal(jobs.size,1);
  assert.equal(calls.filter(item => item.path === '/api/renders').length,1);
});
