import test from 'node:test';
import assert from 'node:assert/strict';
import { webcrypto } from 'node:crypto';
import { createEntreePrint } from '../entree-print.mjs';
import { parseEvents } from '../events.mjs';
import { Clock, flush } from './helpers.mjs';

const json = (body,status=200) => new Response(JSON.stringify(body),{status});
const job = (version,id='one') => ({id,serviceId:'service',version,state:version === 1 ? 'accepted' : 'completed'});
const entry = (sequence,version=sequence) => ({id:`service:${sequence}`,serviceId:'service',type:'job',entityId:'one',version,data:job(version)});
const wire = (type,data,id) => `${id === undefined ? '' : `id: ${id}\n`}event: ${type}\ndata: ${JSON.stringify(data)}\n\n`;
function harness(t) {
  const clock = new Clock(), calls = [], streams = [], received = [];
  const state = {checkpoint:0,printers:[{name:'cashier'}],pages:[],rejectStream:null,checkpointWait:null};
  const api = createEntreePrint({token:'t'.repeat(32)}, {crypto:webcrypto,now:()=>clock.now,setTimeout:clock.setTimeout,clearTimeout:clock.clearTimeout,
    async fetch(url,init) {
      calls.push({url:new URL(url),init});
      assert.equal(init.method,'GET'); // Monitoring cannot submit jobs, commands or renders.
      assert.equal(init.headers.Authorization,'Bearer '+ 't'.repeat(32));
      if (new URL(url).pathname === '/api/connection') return json({serviceId:'service',bootId:'boot',apiVersion:'0.0.1',printers:[{name:'cashier'}]});
      assert.equal(init.headers['X-Entree-Service-ID'],'service');
      if (new URL(url).pathname === '/api/heartbeat') return json({serviceId:'service',bootId:'boot',requestId:new URL(url).searchParams.get('requestId')});
      if (new URL(url).pathname === '/api/events/checkpoint') {
        if (state.checkpointWait) await state.checkpointWait;
        return json({serviceId:'service',cursor:`service:${state.checkpoint}`,printers:state.printers});
      }
      if (new URL(url).pathname === '/api/jobs') return json(state.pages.shift() ?? {items:[],nextCursor:null});
      assert.equal(new URL(url).pathname,'/api/events');
      if (state.rejectStream) { const code=state.rejectStream; state.rejectStream=null; return json({error:{code}},410); }
      let controller;
      const pipe = new ReadableStream({start(value) {controller=value;},cancel() { pipeState.cancelled=true; }});
      const pipeState = {cancelled:false,init,
        frame(type,data,id) {controller.enqueue(new TextEncoder().encode(wire(type,data,id)));},
        event(value) {this.frame(value.type,value,value.id);},end() {controller.close();}
      };
      init.signal.addEventListener('abort',()=> {pipeState.aborted=true; try {controller.error(new DOMException('Aborted','AbortError'));} catch {} });
      streams.push(pipeState);
      return new Response(pipe,{headers:{'Content-Type':'text/event-stream; charset=utf-8'}});
    }});
  t.after(()=>api.disconnect());
  return {api,clock,calls,streams,state,received,
    async start(events=['job','printer']) { const sub=api.subscribe(e=>received.push(e),{events}); await api.connect(); await flush(); return sub; },
    async ready(index=streams.length-1) { const s=streams[index]; s.frame('ready',{serviceId:'service',cursor:s.init.headers['Last-Event-ID']}); await flush(); return s; }
  };
}

test('snapshot races merge by job version and replay advances past suppressed older states',async t=>{
  const h=harness(t); h.state.pages=[{items:[job(2)],nextCursor:null}]; await h.start();
  const s=await h.ready(); s.event(entry(1,1));s.event(entry(2,2));s.event(entry(3,3));
  s.frame('caught-up',{serviceId:'service',cursor:'service:3'});await flush();
  assert.deepEqual(h.received.filter(e=>e.type==='job').map(e=>[e.version,e.snapshot===true]),[[2,true],[3,false]]);
  assert.equal(h.received.at(-1).data.state,'live');
  s.end();await flush();await h.clock.advance(1000);
  assert.equal(h.streams[1].init.headers['Last-Event-ID'],'service:3');
  assert.equal(h.calls.filter(c=>c.url.pathname==='/api/events/checkpoint').length,1);
});

test('pruned history replaces printer inventory and re-reads jobs before reopening the stream',async t=>{
  const h=harness(t); await h.start(); const s=await h.ready();
  h.state.checkpoint=50;h.state.printers=[{name:'kitchen'}];h.state.pages=[{items:[job(9)],nextCursor:null}];
  s.frame('reset',{serviceId:'service',code:'EVENT_CURSOR_EXPIRED'});await flush();await h.clock.advance(1000);
  assert.equal(h.streams[1].init.headers['Last-Event-ID'],'service:50');
  const snapshots=h.received.filter(e=>e.type==='sync' && e.data.state==='synchronizing');
  assert.deepEqual(snapshots.map(e=>e.data.printers.map(p=>p.name)),[['cashier'],['kitchen']]);
  assert.equal(h.received.filter(e=>e.type==='job').at(-1).version,9);
});

test('HTTP expired cursor triggers the same recovery without silently skipping to latest',async t=>{
  const h=harness(t);h.state.rejectStream='EVENT_CURSOR_EXPIRED';await h.start();
  h.state.checkpoint=25;await h.clock.advance(1000);
  assert.equal(h.calls.filter(c=>c.url.pathname==='/api/events/checkpoint').length,2);
  assert.equal(h.streams[0].init.headers['Last-Event-ID'],'service:25');
});

test('wrong owner and sequence gaps never deliver or acknowledge the bad event',async t=>{
  const h=harness(t);await h.start();const s=await h.ready();
  s.event({...entry(1),serviceId:'another'});await flush();await h.clock.advance(1000);
  assert.equal(h.streams[1].init.headers['Last-Event-ID'],'service:0');
  const resumed=await h.ready();resumed.event(entry(2));await flush();await h.clock.advance(2000);
  assert.equal(h.streams[2].init.headers['Last-Event-ID'],'service:0');
  assert.equal(h.received.filter(e=>e.type==='job').length,0);
});

test('explicit removed printer tombstones reach the client and duplicates are ignored',async t=>{
  const h=harness(t);await h.start();const s=await h.ready();
  const removed={id:'service:1',serviceId:'service',type:'printer',entityId:'cashier',version:1,data:{name:'cashier',removed:true}};
  s.event(removed);s.event(removed);await flush();
  assert.equal(h.received.filter(e=>e.type==='printer' && e.data.removed).length,1);
});

test('subscriptions share one stream per verified service and one listener cannot corrupt another',async t=>{
  const h=harness(t);await h.start();await h.ready();
  const broken=h.api.subscribe(e=>{e.data.state='mutated';throw Error('POS view failed');},{events:['job']});
  await flush();await h.clock.advance(0);const s=await h.ready();
  await h.api.connect({ip:'another-address'});await flush();await h.clock.advance(0);
  assert.equal(h.streams.filter(stream=>!stream.aborted).length,1);
  const current=await h.ready();current.event(entry(1));await flush();
  assert.equal(h.received.filter(e=>e.type==='job').at(-1).data.state,'accepted');
  broken.close();assert.equal(h.streams.filter(stream=>!stream.aborted).length,1);
});

test('closing the last remote subscription aborts reading and leaves only heartbeat monitoring',async t=>{
  const h=harness(t);const sub=await h.start();await h.ready();sub.close();await flush();
  assert.equal(h.streams[0].aborted,true);
  assert.equal(h.clock.timers.size,1);
  h.api.disconnect();assert.equal(h.clock.timers.size,0);
});

test('closing during snapshot I/O ignores its late result and opens no stream',async t=>{
  const h=harness(t);let release;h.state.checkpointWait=new Promise(resolve=>{release=resolve;});
  const sub=await h.start();sub.close();const count=h.received.length;release();await flush();
  assert.equal(h.received.length,count);assert.equal(h.streams.length,0);
});

test('silent event streams time out and resume from the last acknowledged event',async t=>{
  const h=harness(t);await h.start();const s=await h.ready();s.event(entry(1));await flush();
  await h.clock.advance(31000);
  assert.equal(s.aborted,true);assert.equal(h.streams[1].init.headers['Last-Event-ID'],'service:1');
});

test('snapshot pagination is unfiltered, bounded and completed before replay',async t=>{
  const h=harness(t);h.state.pages=[{items:[job(1)],nextCursor:'page-2'},{items:[job(3,'two')],nextCursor:null}];
  await h.start();
  assert.deepEqual(h.received.filter(e=>e.type==='job').map(e=>e.entityId),['one','two']);
  assert.deepEqual(h.calls.filter(c=>c.url.pathname==='/api/jobs').map(c=>c.url.search),['?limit=100','?limit=100&cursor=page-2']);
});

test('parser handles every byte split, Chinese UTF-8, CRLF and multiline data',async()=>{
  const bytes=new TextEncoder().encode('event: job\r\nid: service:1\r\ndata: {"text":\r\ndata: "厨房"}\r\n\r\n');
  for(let split=0;split<=bytes.length;split++) {
    const chunks=[bytes.slice(0,split),bytes.slice(split)];
    const reader=new ReadableStream({start(c){for(const chunk of chunks)c.enqueue(chunk);c.close();}}).getReader();
    const results=[];for await(const value of parseEvents(reader))results.push(value);
    assert.deepEqual(results,[{type:'job',id:'service:1',data:'{"text":\n"厨房"}'}]);
  }
});

test('parser discards incomplete EOF events and rejects excessive or corrupt frames',async()=>{
  async function parse(bytes) {
    const reader=new ReadableStream({start(c){c.enqueue(bytes);c.close();}}).getReader();
    const rows=[];for await(const value of parseEvents(reader))rows.push(value);return rows;
  }
  assert.deepEqual(await parse(new TextEncoder().encode('id: service:1\ndata: incomplete\n')),[]);
  await assert.rejects(parse(new TextEncoder().encode('data: '+'x'.repeat(2100001))));
  await assert.rejects(parse(Uint8Array.from([0xff])));
});
