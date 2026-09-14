// Read-only status recovery. No render, submission or outbox operation belongs here.
export function createEventMonitor(session, io, emit, ErrorType) {
  let stopped = false, running = false, controller, timer, cursor = null, failures = 0;
  let generation = 0, endpoint = session.config.baseUrl;
  let jobVersions = new Map();
  const error = (code, message) => new ErrorType(code, message);
  const valid = run => !stopped && generation === run && !session.disabled;
  function position(value) {
    const prefix = session.identity + ':';
    if (typeof value !== 'string' || !value.startsWith(prefix)) throw error('SERVICE_MISMATCH', 'The event cursor belongs to another service.');
    const suffix = value.slice(prefix.length);
    if (!/^(0|[1-9][0-9]{0,18})$/.test(suffix) || BigInt(suffix) > 9223372036854775807n)
      throw error('EVENT_CURSOR_INVALID', 'The service returned an invalid event cursor.');
    return BigInt(suffix);
  }
  function publish(event, run) { if (valid(run)) emit(event); }
  function sync(state, run, extra = {}) { publish({ type:'sync', serviceId:session.identity, data:{state, ...extra} },run); }
  function job(value) {
    if (!value || value.serviceId !== session.identity || typeof value.id !== 'string' || !value.id ||
      !Number.isSafeInteger(value.version) || value.version < 1)
      throw error('EVENT_INVALID', 'The service returned an invalid job snapshot.');
  }
  async function reconcile(run) {
    const checkpoint = await session.read('/api/events/checkpoint');
    if (!valid(run)) return;
    if (checkpoint?.serviceId !== session.identity || !Array.isArray(checkpoint.printers) ||
      checkpoint.printers.some(p => typeof p?.name !== 'string' || !p.name))
      throw error('EVENT_INVALID', 'The service returned an invalid printer checkpoint.');
    position(checkpoint.cursor);
    const versions = new Map();
    // The complete printer inventory and its cursor are captured under one server lock.
    sync('synchronizing',run,{printers:checkpoint.printers});
    for (const printer of checkpoint.printers)
      publish({type:'printer',serviceId:session.identity,entityId:printer.name,version:null,id:null,snapshot:true,data:printer},run);
    let next = null, pages = 0;
    do {
      const page = await session.read('/api/jobs?' + new URLSearchParams({limit:'100',...(next ? {cursor:next} : {})}));
      if (!valid(run)) return;
      if (!Array.isArray(page?.items) || page.items.length > 100 ||
        (page.nextCursor !== null && (typeof page.nextCursor !== 'string' || !page.nextCursor || page.nextCursor === next)) || ++pages > 1001)
        throw error('EVENT_INVALID', 'The service returned invalid job history while recovering status.');
      for (const value of page.items) {
        job(value);
        if (value.version > (versions.get(value.id) ?? 0)) {
          versions.set(value.id,value.version);
          publish({type:'job',serviceId:session.identity,entityId:value.id,version:value.version,id:null,snapshot:true,data:value},run);
        }
      }
      next = page.nextCursor;
    } while (next);
    if (!valid(run)) return;
    jobVersions = versions; cursor = checkpoint.cursor;
    // Jobs use stable acceptance-order pagination, then replay from the earlier
    // checkpoint. Version checks suppress replay older than a page observation.
  }
  async function stream(run) {
    controller = new AbortController();
    const abort = () => controller?.abort();
    session.lifetime.signal.addEventListener('abort',abort,{once:true});
    let watchdog, reader;
    const arm = () => { io.clearTimeout(watchdog); watchdog = io.setTimeout(abort,30000); watchdog?.unref?.(); };
    try {
      arm();
      const response = await io.fetch(session.config.baseUrl + '/api/events', {
        method:'GET', headers:{Authorization:`Bearer ${session.config.token}`,'X-Entree-Service-ID':session.identity,
          'Last-Event-ID':cursor, Accept:'text/event-stream'}, signal:controller.signal,
        cache:'no-store',credentials:'omit',redirect:'error'
      });
      if (!valid(run)) { await response.body?.cancel(); return; }
      if (!response.ok) {
        const body = await response.json();
        throw error(body?.error?.code ?? 'EVENT_UNAVAILABLE',body?.error?.message ?? 'Status updates are unavailable.');
      }
      if (response.headers.get('content-type')?.split(';')[0].trim() !== 'text/event-stream' || !response.body)
        throw error('EVENT_INVALID','The service did not return an event stream.');
      reader = response.body.getReader();
      let ready = false;
      for await (const frame of parseEvents(reader)) {
        if (!valid(run)) return;
        arm();
        let value;
        try { value = JSON.parse(frame.data); } catch { throw error('EVENT_INVALID','An event contained invalid JSON.'); }
        if (value?.serviceId !== session.identity) throw error('SERVICE_MISMATCH','An event belongs to another service.');
        if (frame.type === 'ready') {
          if (ready || value.cursor !== cursor || frame.id !== undefined) throw error('EVENT_INVALID','The stream resumed at an unexpected cursor.');
          ready = true; sync('replaying',run); continue;
        }
        if (!ready) throw error('EVENT_INVALID','The stream did not confirm its resume cursor.');
        if (frame.type === 'caught-up') {
          if (value.cursor !== cursor || frame.id !== undefined) throw error('EVENT_INVALID','The stream checkpoint does not match replay.');
          failures = 0; sync('live',run); continue;
        }
        if (frame.type === 'heartbeat') { if (frame.id !== undefined) throw error('EVENT_INVALID','Heartbeat cannot advance a cursor.'); continue; }
        if (frame.type === 'reset') throw error('EVENT_CURSOR_EXPIRED','Status history was pruned.');
        if (frame.type === 'reconnect') throw error('EVENT_STREAM_GAP','The status stream needs to reconnect.');
        if (!['job','printer'].includes(frame.type) || value.type !== frame.type || value.id !== frame.id ||
          typeof value.entityId !== 'string' || !value.entityId || !Number.isSafeInteger(value.version) || value.version < 1)
          throw error('EVENT_INVALID','The event identity or version is invalid.');
        const next = position(frame.id), prior = position(cursor);
        if (next <= prior) continue; // A transport may repeat already acknowledged events.
        if (next !== prior + 1n) throw error('EVENT_STREAM_GAP','The event sequence has a gap.');
        if (frame.type === 'job') {
          job(value.data);
          if (value.data.id !== value.entityId || value.data.version !== value.version) throw error('EVENT_INVALID','The job event does not match its envelope.');
          if (value.version > (jobVersions.get(value.entityId) ?? 0)) {
            jobVersions.set(value.entityId,value.version); publish(value,run);
          }
        } else {
          if (value.data?.name !== value.entityId) throw error('EVENT_INVALID','The printer event does not match its envelope.');
          publish(value,run);
        }
        cursor = frame.id;
      }
      throw error('EVENT_STREAM_GAP','The status stream ended.');
    } finally {
      io.clearTimeout(watchdog); session.lifetime.signal.removeEventListener('abort',abort);
      controller?.abort();
      if (reader) { try { await reader.cancel(); } catch {} reader.releaseLock(); }
    }
  }
  async function run() {
    if (running || stopped || session.disabled) return;
    running = true; const started = generation;
    try {
      if (cursor === null) await reconcile(started);
      if (valid(started)) await stream(started);
    } catch (failure) {
      if (valid(started)) {
        if (failure.code === 'EVENT_CURSOR_EXPIRED') cursor = null;
        sync('reconnecting',started,{code:failure.code ?? 'SERVICE_UNAVAILABLE'});
        failures++;
      }
    } finally {
      running = false;
      if (!stopped && !session.disabled) {
        const delay = generation !== started ? 0 : Math.min(30000,500 * 2 ** Math.min(failures,6));
        timer = io.setTimeout(() => { void run(); },delay); timer?.unref?.();
      }
    }
  }
  return {
    start() { void run(); },
    refresh() { generation++; cursor = null; controller?.abort(); io.clearTimeout(timer); if (!running) void run(); },
    wake() {
      if (endpoint === session.config.baseUrl) return;
      endpoint = session.config.baseUrl; generation++; controller?.abort(); io.clearTimeout(timer); if (!running) void run();
    },
    close() { stopped = true; generation++; controller?.abort(); io.clearTimeout(timer); jobVersions.clear(); }
  };
}

// UTF-8 and line boundaries may split anywhere, including inside Chinese text
// or CRLF. Incomplete EOF frames never acknowledge an event.
export async function* parseEvents(reader) {
  const decoder = new TextDecoder('utf-8',{fatal:true});
  let buffer = '', data = [], type = 'message', id, size = 0, cr = false;
  for (;;) {
    const chunk = await reader.read();
    if (chunk.done) { decoder.decode(); return; }
    if (!chunk.value.byteLength) continue;
    buffer += decoder.decode(chunk.value,{stream:true});
    if (cr && buffer.startsWith('\n')) buffer = buffer.slice(1);
    cr = false;
    let match;
    while ((match = /[\r\n]/.exec(buffer))) {
      const line = buffer.slice(0,match.index), ending = buffer[match.index];
      buffer = buffer.slice(match.index+1);
      if (ending === '\r') { if (buffer.startsWith('\n')) buffer = buffer.slice(1); else if (!buffer.length) cr = true; }
      size += line.length;
      if (size > 2100000) throw new Error('Event frame exceeds its size limit.');
      if (!line) {
        if (data.length) yield {type,id,data:data.join('\n')};
        data = []; type = 'message'; id = undefined; size = 0;
      } else if (!line.startsWith(':')) {
        const colon = line.indexOf(':');
        const field = colon < 0 ? line : line.slice(0,colon);
        const value = colon < 0 ? '' : line.slice(colon+1).replace(/^ /,'');
        if (field === 'data') data.push(value);
        else if (field === 'event') type = value;
        else if (field === 'id' && !value.includes('\0')) id = value;
      }
    }
    if (buffer.length + size > 2100000) throw new Error('Event frame exceeds its size limit.');
  }
}
