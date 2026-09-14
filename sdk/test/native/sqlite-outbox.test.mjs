import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, rm, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fork } from 'node:child_process';
import { once } from 'node:events';
import { DatabaseSync } from 'node:sqlite';
import { webcrypto } from 'node:crypto';
import { createSqliteOutboxStorage } from '../../sqlite-outbox.mjs';
import { createEntreePrint } from '../../entree-print.mjs';
import { createNativeEntreePrint } from '../../native.mjs';

const record = () => ({ serviceId:'owner',idempotencyKey:'one',printer:'cashier',fingerprint:'source',content:'厨房',state:'pending',wire:null });
async function directory(t) {
  const path = await mkdtemp(join(tmpdir(),'EntreeSqliteOutbox-'));
  t.after(() => rm(path,{recursive:true,force:true}));
  return path;
}
function child(path,mode) {
  const process = fork(new URL('./sqlite-child.mjs',import.meta.url),[path,mode],{stdio:['ignore','ignore','pipe','ipc']});
  let stderr = '';
  process.stderr.on('data',chunk => { stderr += chunk; });
  const exited = once(process,'exit').then(([code,signal]) => ({code,signal,stderr}));
  return {process,exited};
}

test('SQLite persists copied records and exact wire across a new store', async t => {
  const path = await directory(t), store = await createSqliteOutboxStorage({directory:path});
  const value = record(); const saving = store.add(value); value.content = 'mutated';
  const saved = await saving;
  assert.equal(saved.content,'厨房');
  await store.update(saved,{...saved,state:'submitting',wire:{json:'exact bytes',digest:'digest'}});
  store.close();
  const reopened = await createSqliteOutboxStorage({directory:path});
  const [result] = await reopened.list();
  assert.equal(result.state,'submitting'); assert.equal(result.wire.json,'exact bytes');
  result.wire.json = 'changed'; assert.equal((await reopened.list())[0].wire.json,'exact bytes');
  reopened.close();
});

test('SQLite rejects stale updates and owner changes without changing the stored intent', async t => {
  const store = await createSqliteOutboxStorage({directory:await directory(t)});
  const saved = await store.add(record());
  const current = await store.update(saved,{...saved,state:'prepared'});
  await assert.rejects(store.update(saved,{...saved,state:'accepted'}),{code:'OUTBOX_CONFLICT'});
  await assert.rejects(store.update(current,{...current,serviceId:'other'}),{code:'OUTBOX_CONFLICT'});
  assert.equal((await store.list())[0].state,'prepared'); store.close();
});

test('two native processes atomically deduplicate an intent', async t => {
  const path = await directory(t);
  const first = child(path,'add'), second = child(path,'add');
  const results = await Promise.all([first.exited,second.exited]);
  for (const result of results) assert.equal(result.code,0,result.stderr);
  const store = await createSqliteOutboxStorage({directory:path});
  assert.equal((await store.list()).length,1); store.close();
});

test('process crash releases only its queue lock and preserves previously committed wire', {timeout:10000}, async t => {
  const path = await directory(t);
  const owner = child(path,'hold');
  t.after(async () => { if (owner.process.exitCode === null && owner.process.signalCode === null) owner.process.kill(); await owner.exited; });
  const ready = once(owner.process,'message');
  await Promise.race([ready,owner.exited.then(result => { throw new Error(result.stderr || 'Child exited before acquiring its queue'); })]);
  const store = await createSqliteOutboxStorage({directory:path,lockTimeoutMs:100});
  let entered = false;
  await assert.rejects(store.withQueue('owner','cashier',() => { entered = true; }),{code:'OUTBOX_BUSY'});
  assert.equal(entered,false);
  assert.equal(await store.withQueue('owner','kitchen',() => 'independent'),'independent');
  assert.equal((await store.list())[0].state,'submitting'); // Visible before the lock-owning process exits.
  owner.process.kill(); await owner.exited;
  await store.withQueue('owner','cashier',async () => {
    const [saved] = await store.list();
    assert.deepEqual(saved.wire,{json:'exact saved bytes',digest:'saved digest'});
  });
  store.close();
});

test('a failed dispatch releases the lock and closing active storage is rejected', async t => {
  const store = await createSqliteOutboxStorage({directory:await directory(t)});
  await assert.rejects(store.withQueue('owner','cashier',() => {
    assert.throws(() => store.close(),{code:'OUTBOX_BUSY'});
    throw new Error('dispatch failed');
  }),/dispatch failed/);
  await store.withQueue('owner','cashier',() => {});
  store.close(); await assert.rejects(store.list(),{code:'OUTBOX_CLOSED'});
});

test('corrupt or unsupported databases are not reset', async t => {
  const path = await directory(t), file = join(path,'intents.sqlite');
  await writeFile(file,'retained corrupt evidence');
  await assert.rejects(createSqliteOutboxStorage({directory:path}));
  assert.equal(await readFile(file,'utf8'),'retained corrupt evidence');
  const other = await directory(t), database = new DatabaseSync(join(other,'intents.sqlite'));
  database.exec('PRAGMA user_version=2'); database.close();
  await assert.rejects(createSqliteOutboxStorage({directory:other}),{code:'OUTBOX_UNSUPPORTED'});
});

test('full storage preserves keys and still returns an existing intent', async t => {
  const path = await directory(t), store = await createSqliteOutboxStorage({directory:path});
  await store.add(record());
  const database = new DatabaseSync(join(path,'intents.sqlite'));
  database.exec('BEGIN IMMEDIATE');
  const insert = database.prepare('INSERT INTO intents(service_id,intent_key,version,fingerprint,record) VALUES(?,?,1,?,?)');
  for (let index=1; index<10000; index++) insert.run('owner',String(index),'source','{}');
  database.exec('COMMIT'); database.close();
  await assert.rejects(store.add({...record(),idempotencyKey:'overflow'}),{code:'OUTBOX_FULL'});
  assert.equal((await store.add(record())).idempotencyKey,'one'); store.close();
});

test('native factory uses its supplied SQLite store before any connection', async t => {
  const store = await createSqliteOutboxStorage({directory:await directory(t)});
  const api = createNativeEntreePrint({}, {outboxStorage:store});
  await api.outbox().enqueue({serviceId:'owner',printer:'cashier',idempotencyKey:'native',content:'<p>Native</p>'});
  assert.equal((await store.list())[0].state,'pending'); api.disconnect(); store.close();
});

test('a new SDK and SQLite store recover a lost acknowledgement using identical saved bytes', async t => {
  const path = await directory(t), wires = []; let renders = 0, loseAck = true;
  const client = async () => {
    const storage = await createSqliteOutboxStorage({directory:path});
    const api = createEntreePrint({token:'t'.repeat(32),retryCount:0},{crypto:webcrypto,outboxStorage:storage,
      fetch:async (url,options) => {
        const route = new URL(url).pathname;
        const body = options.body && JSON.parse(new TextDecoder().decode(options.body));
        const json = value => new Response(JSON.stringify(value),{headers:{'X-Entree-Content-SHA256':options.headers['X-Entree-Content-SHA256'] || ''}});
        if (route === '/api/connection') return json({serviceId:'owner',bootId:'boot',apiVersion:'0.0.1',printers:[{name:'cashier'}]});
        if (route === '/api/renders') { renders++; return json({id:'render',serviceId:'owner',html:body.html,expiresAt:new Date(Date.now()+60000).toISOString()}); }
        assert.equal(route,'/api/jobs');
        wires.push(new TextDecoder().decode(options.body));
        const [saved] = await storage.list();
        assert.equal(saved.state,'submitting'); assert.equal(saved.wire.json,wires.at(-1));
        if (loseAck) throw new TypeError('Lost ACK');
        return json({id:'original-job',serviceId:'owner',idempotencyKey:body.idempotencyKey,state:'accepted',integrity:{verified:true}});
      }});
    return {api,storage};
  };
  const first = await client();
  await first.api.outbox().enqueue({serviceId:'owner',printer:'cashier',idempotencyKey:'original',content:'<p>厨房</p>'});
  assert.equal((await first.api.outbox().flush(await first.api.connect()))[0].state,'uncertain');
  first.api.disconnect(); first.storage.close(); loseAck = false;
  const next = await client(), concurrent = await client();
  try {
    const libraries = await Promise.all([next.api.connect(),concurrent.api.connect()]);
    const outcomes = (await Promise.all([next.api.outbox().flush(libraries[0]),concurrent.api.outbox().flush(libraries[1])])).flat();
    assert.equal(outcomes.length,1); assert.equal(outcomes[0].job.id,'original-job');
    assert.equal(renders,1); assert.equal(wires.length,2); assert.equal(wires[0],wires[1]);
  } finally { next.api.disconnect(); next.storage.close(); concurrent.api.disconnect(); concurrent.storage.close(); }
});
