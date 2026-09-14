import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, webcrypto } from 'node:crypto';
import { createEntreePrint } from '../entree-print.mjs';

const node = serviceId => ({ip:serviceId, serviceId, token:`${serviceId}-credential-`.repeat(4)});
const job = serviceId => ({id:'same-job-id',serviceId,idempotencyKey:'same-intent',renderId:`${serviceId}-render`,
  status:'completed',printer:'Kitchen',metadata:{orderID:'order-1'}});

function harness(t) {
  const calls = [], state = { primary:{}, backup:{} };
  const api = createEntreePrint({retryCount:0,heartbeat:{intervalMs:60000}}, {crypto:webcrypto,fetch:async (url,init)=>{
    const address = new URL(url), name = address.hostname, server = state[name];
    assert.ok(server, 'Job data must not supply a new endpoint');
    assert.equal(init.headers.Authorization,`Bearer ${server.token ?? node(name).token}`);
    const body = init.body && JSON.parse(new TextDecoder().decode(init.body));
    calls.push({name,path:address.pathname,query:address.search,body,bytes:init.body && Buffer.from(init.body).toString('hex')});
    if (server.offline) throw new TypeError('Owner offline');
    const json = (value,status=200) => new Response(JSON.stringify(value),{status,headers:{
      'X-Entree-Content-SHA256':init.headers['X-Entree-Content-SHA256'] ?? ''}});
    if (address.pathname === '/api/connection') return json({serviceId:name,bootId:'boot',apiVersion:'0.0.1',printers:[{name:'Kitchen'}]});
    assert.equal(init.headers['X-Entree-Service-ID'],name);
    if (server.reject) return json({error:{code:server.reject,message:server.reject,delivery:'not_sent'}},404);
    if (server.response) return json(server.response);
    if (address.pathname.endsWith('/reprints')) {
      assert.equal(createHash('sha256').update(init.body).digest('hex'),init.headers['X-Entree-Content-SHA256']);
      if (server.loseAck) throw new TypeError('Acknowledgement lost');
      return json({...job(name),id:'new-copy',reprintOf:'same-job-id',idempotencyKey:body.idempotencyKey,integrity:{verified:true}},202);
    }
    if (address.pathname.endsWith('/render')) return json({id:`${name}-render`,serviceId:name,html:`<p>${name}</p>`});
    assert.ok(['/api/jobs/same-job-id','/api/jobs/lookup'].includes(address.pathname));
    return json(job(name));
  }});
  t.after(()=>api.disconnect());
  return {api,state,calls};
}

test('job objects resolve status, retained preview and explicit reprint to the original owner after selection changes', async t=>{
  const {api,calls} = harness(t);
  await api.connect(node('primary'));
  const other = await api.connect(node('backup'));
  const saved = job('primary');
  // Unrelated job fields are ignored; addresses and tokens never come from them.
  saved.ip = 'untrusted-host'; saved.token = 'untrusted-token';
  const start = calls.length;
  assert.equal((await other.getJob(saved)).serviceId,'primary');
  assert.equal((await api.getJobRender(saved)).html,'<p>primary</p>');
  assert.equal((await other.getJob({serviceId:'primary',idempotencyKey:'same-intent'})).serviceId,'primary');
  assert.equal((await other.reprintJob(saved,{idempotencyKey:'operator-copy'})).serviceId,'primary');
  assert.ok(calls.slice(start).every(call=>call.name==='primary'));
  assert.equal(other.connection.serviceId,'backup');
  assert.equal(saved.status,'completed');
});

test('bare IDs stay on their library while identical IDs with explicit owners remain distinct', async t=>{
  const {api,calls} = harness(t);
  const primary = await api.connect(node('primary'));
  const backup = await api.connect(node('backup'));
  assert.equal((await primary.getJob('same-job-id')).serviceId,'primary');
  assert.equal((await backup.getJob('same-job-id')).serviceId,'backup');
  assert.equal((await primary.getJob(job('backup'))).serviceId,'backup');
  assert.deepEqual(calls.slice(-3).map(call=>call.name),['primary','backup','backup']);
});

test('unconnected owners and malformed references cause no requests or endpoint guessing', async t=>{
  const {api,calls} = harness(t);
  const backup = await api.connect(node('backup')); const start = calls.length;
  for (const method of ['getJob','getJobRender']) {
    assert.throws(()=>backup[method](job('primary')),{code:'JOB_OWNER_REQUIRED',delivery:'unknown'});
    for (const bad of [{id:'same-job-id'}, {id:'same-job-id',serviceId:''}, {id:[],serviceId:'primary'}, []])
      assert.throws(()=>backup[method](bad),error=>['REQUEST_INVALID','FIELD_UNSUPPORTED'].includes(error.code));
  }
  await assert.rejects(backup.reprintJob(job('primary'),{idempotencyKey:'copy'}),{code:'JOB_OWNER_REQUIRED'});
  assert.throws(()=>backup.getJob({serviceId:'primary',idempotencyKey:'same-intent'}),{code:'JOB_OWNER_REQUIRED'});
  assert.equal(calls.length,start);
});

for (const method of ['getJob','getJobRender']) test(`${method} keeps an offline or missing original job uncertain without consulting the backup`,async t=>{
  const {api,state,calls} = harness(t);
  await api.connect(node('primary')); const backup = await api.connect(node('backup'));
  const start = calls.length;
  state.primary.offline = true;
  await assert.rejects(backup[method](job('primary')),error=>{
    assert.equal(error.delivery,'unknown'); assert.equal(error.details.serviceId,'primary');
    assert.equal(error.details.jobId,'same-job-id'); return true;
  });
  state.primary.offline = false; state.primary.reject = 'JOB_NOT_FOUND';
  await assert.rejects(backup[method](job('primary')),{code:'JOB_NOT_FOUND',delivery:'unknown'});
  assert.ok(calls.slice(start).every(call=>call.name==='primary' && !call.body));
});

for (const variant of ['wrong-owner','wrong-job','missing-owner','wrong-render']) test(`owner recovery rejects a ${variant} response`,async t=>{
  const {api,state} = harness(t); const print = await api.connect(node('primary'));
  const rendered = variant === 'wrong-render';
  state.primary.response = rendered ? {serviceId:'primary',id:'other-render',html:'<p>Wrong receipt</p>'} : {...job('primary')};
  if (variant==='wrong-owner') state.primary.response.serviceId='backup';
  if (variant==='wrong-job') state.primary.response.id='another-job';
  if (variant==='missing-owner') delete state.primary.response.serviceId;
  await assert.rejects(print[rendered?'getJobRender':'getJob'](job('primary')),error=>{
    assert.ok(['SERVICE_MISMATCH','RESPONSE_INVALID'].includes(error.code));
    assert.equal(error.delivery,'unknown'); assert.equal(error.details.serviceId,'primary'); return true;
  });
});

test('a job accepted on an initially selected backup can be explicitly reprinted on that exact owner',async t=>{
  const {api,state,calls} = harness(t); state.primary.offline=true;
  const print = await api.connect({nodes:[node('primary'),node('backup')]});
  assert.equal(print.connection.nodeSelection.usedBackup,true);
  await assert.rejects(print.reprintJob('same-job-id',{idempotencyKey:'copy'}),{code:'JOB_OWNER_REQUIRED'});
  const start = calls.length;
  const copy = await print.reprintJob(job('backup'),{idempotencyKey:'copy'});
  assert.equal(copy.serviceId,'backup'); assert.equal(copy.reprintOf,'same-job-id');
  assert.ok(calls.slice(start).every(call=>call.name==='backup'));
});

test('lost reprint acknowledgement keeps the explicit owner and identical wire across later top-level selections',async t=>{
  const {api,state,calls} = harness(t); await api.connect(node('primary'));
  state.primary.loseAck = true;
  await assert.rejects(api.reprintJob(job('primary'),{idempotencyKey:'copy'}),{delivery:'unknown'});
  await api.connect(node('backup')); state.primary.loseAck=false;
  await api.reprintJob(job('primary'),{idempotencyKey:'copy'});
  const posts = calls.filter(call=>call.body);
  assert.equal(posts.length,2); assert.ok(posts.every(call=>call.name==='primary'));
  assert.equal(posts[0].bytes,posts[1].bytes);
});

test('disconnect forgets automatic owner resolution until that owner is connected again',async t=>{
  const {api,calls} = harness(t); await api.connect(node('primary')); api.disconnect();
  const backup = await api.connect(node('backup')); const start = calls.length;
  assert.throws(()=>backup.getJob(job('primary')),{code:'JOB_OWNER_REQUIRED'});
  assert.equal(calls.length,start);
  await api.connect(node('primary'));
  assert.equal((await backup.getJob(job('primary'))).serviceId,'primary');
});

test('cross-owner lookup uses the most recently verified credentials for that owner',async t=>{
  const {api,state} = harness(t); await api.connect(node('primary'));
  state.primary.token = 'rotated-owner-token-'.repeat(3);
  await api.connect({...node('primary'),token:state.primary.token});
  const backup = await api.connect(node('backup'));
  assert.equal((await backup.getJob(job('primary'))).serviceId,'primary');
  assert.equal((await backup.reprintJob(job('primary'),{idempotencyKey:'copy'})).serviceId,'primary');
});
