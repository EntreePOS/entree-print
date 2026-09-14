import { createIndexedDbOutboxStorage, createOutboxController } from './outbox.mjs';
import { createEventMonitor } from './events.mjs';

const DIGEST = 'X-Entree-Content-SHA256';
const SERVICE = 'X-Entree-Service-ID';
const defaults = { ip: '127.0.0.1', port: 9779, protocol: 'http', token: '', requestTimeoutMs: 10000,
  renderTimeoutMs: 30000, retryCount: 1, retryDelayMs: 250,
  heartbeat: { intervalMs: 5000, timeoutMs: 2000, missedLimit: 3 } };

export class EntreePrintError extends Error {
  constructor(code, message, options = {}) {
    super(message);
    this.name = 'EntreePrintError';
    this.code = code;
    this.retryable = options.retryable ?? false;
    this.delivery = options.delivery ?? 'not_sent';
    this.details = options.details;
  }
}

function fail(code, message, options) { throw new EntreePrintError(code, message, options); }
function fields(value, allowed) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) fail('REQUEST_INVALID', 'Expected an options object.');
  for (const key of Object.keys(value)) if (!allowed.includes(key)) fail('FIELD_UNSUPPORTED', `Unsupported option: ${key}.`);
}
function text(value, name, max = 200) {
  if (typeof value !== 'string' || !value.length || value.length > max) fail('REQUEST_INVALID', `${name} must be a nonempty string of at most ${max} characters.`);
  return value;
}
function number(value, name, min, max) {
  if (!Number.isFinite(value) || value < min || value > max) fail('REQUEST_INVALID', `${name} must be between ${min} and ${max}.`);
  return value;
}
function snapshot(value) {
  try { return JSON.parse(JSON.stringify(value)); }
  catch { fail('REQUEST_INVALID', 'Content and metadata must be JSON-serializable.'); }
}
function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]));
  return value;
}
function settings(previous, options) {
  fields(options, Object.keys(defaults));
  const result = { ...previous, ...options, heartbeat: { ...previous.heartbeat, ...options.heartbeat } };
  if (options.heartbeat !== undefined) fields(options.heartbeat, ['intervalMs', 'timeoutMs', 'missedLimit']);
  text(result.ip, 'ip', 253);
  if (/[/\\?#@\s]/.test(result.ip)) fail('REQUEST_INVALID', 'ip must be a host name or IP address, without a URL path.');
  if (!['http', 'https'].includes(result.protocol)) fail('REQUEST_INVALID', 'protocol must be http or https.');
  number(result.port, 'port', 1, 65535);
  if (!Number.isInteger(result.port)) fail('REQUEST_INVALID', 'port must be an integer.');
  if (typeof result.token !== 'string') fail('REQUEST_INVALID', 'token must be a string.');
  for (const key of ['requestTimeoutMs', 'renderTimeoutMs']) number(result[key], key, 1, 120000);
  number(result.retryCount, 'retryCount', 0, 3);
  if (!Number.isInteger(result.retryCount)) fail('REQUEST_INVALID', 'retryCount must be an integer.');
  number(result.retryDelayMs, 'retryDelayMs', 0, 30000);
  for (const key of ['intervalMs', 'timeoutMs']) number(result.heartbeat[key], key, 1, 60000);
  number(result.heartbeat.missedLimit, 'missedLimit', 1, 20);
  if (!Number.isInteger(result.heartbeat.missedLimit)) fail('REQUEST_INVALID', 'missedLimit must be an integer.');
  const host = result.ip.includes(':') && !result.ip.startsWith('[') ? `[${result.ip}]` : result.ip;
  try { result.baseUrl = new URL(`${result.protocol}://${host}:${result.port}`).origin; }
  catch { fail('REQUEST_INVALID', 'Invalid print-service address.'); }
  return result;
}

export function createEntreePrint(options = {}, dependencies = {}) {
  const io = { fetch: (...args) => globalThis.fetch(...args), crypto: globalThis.crypto,
    now: () => Date.now(), setTimeout: (...args) => globalThis.setTimeout(...args),
    clearTimeout: (...args) => globalThis.clearTimeout(...args), ...dependencies };
  const listeners = new Set();
  const eventMonitors = new Map();
  const sessions = new Set();
  const monitors = new Map();
  const discoveries = new Set();
  let configuration = settings(defaults, options);
  let current;
  let sequence = 0;
  let selection = 0;
  let handshakeSequence = 0;
  let unsubscribeResume = null;
  let outbox;

  function watchResume() {
    if (unsubscribeResume) return;
    const wake = () => { for (const session of sessions) session.wake().catch(() => {}); };
    if (io.subscribeResume) { unsubscribeResume = io.subscribeResume(wake); return; }
    if (!globalThis.addEventListener) return;
    const visible = () => { if (globalThis.document?.visibilityState === 'visible') wake(); };
    globalThis.addEventListener('pageshow', wake);
    globalThis.addEventListener('focus', wake);
    globalThis.document?.addEventListener('visibilitychange', visible);
    unsubscribeResume = () => {
      globalThis.removeEventListener('pageshow', wake); globalThis.removeEventListener('focus', wake);
      globalThis.document?.removeEventListener('visibilitychange', visible);
    };
  }

  function emit(data) {
    const event = { type: 'connection', sequence: ++sequence, data: snapshot(data) };
    deliver(event);
  }
  function deliver(event) {
    for (const listener of listeners) if (listener.events.includes(event.type) || (event.type === 'sync' && listener.remote)) {
      try { listener.handler(snapshot(event)); } catch { /* A POS view must not break monitoring. */ }
    }
  }
  function updateEventMonitors(refresh = false) {
    const wanted = [...listeners].some(listener => listener.remote);
    if (!wanted) { for (const monitor of eventMonitors.values()) monitor.close(); eventMonitors.clear(); return; }
    for (const owner of new Set([...monitors.values()])) {
      if (owner.disabled) continue;
      let monitor = eventMonitors.get(owner);
      if (!monitor) {
        monitor = createEventMonitor(owner,io,deliver,EntreePrintError);
        eventMonitors.set(owner,monitor); monitor.start();
      } else if (refresh) monitor.refresh();
      else monitor.wake();
    }
  }
  function randomKey() {
    if (!io.crypto?.getRandomValues) fail('CRYPTO_UNAVAILABLE', 'A cryptographic random source is required to create a print intent.');
    const bytes = io.crypto.getRandomValues(new Uint8Array(16));
    bytes[6] = (bytes[6] & 15) | 64; bytes[8] = (bytes[8] & 63) | 128;
    const hex = [...bytes].map(byte => byte.toString(16).padStart(2, '0')).join('');
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  }
  async function encode(body) {
    if (!io.crypto?.subtle) fail('CRYPTO_UNAVAILABLE', 'Web Crypto is required. Use a secure POS context or the native crypto adapter.');
    const bytes = new TextEncoder().encode(JSON.stringify(body));
    if (bytes.length > 1000000) fail('REQUEST_TOO_LARGE', 'The encoded request exceeds 1 MB.');
    const hash = new Uint8Array(await io.crypto.subtle.digest('SHA-256', bytes));
    return { bytes, digest: [...hash].map(byte => byte.toString(16).padStart(2, '0')).join('') };
  }

  class Session {
    constructor(config, tracked = true) {
      this.config = config; this.identity = null; this.info = null; this.connecting = null;
      this.lifetime = new AbortController(); this.disabled = false;
      this.timer = null; this.polling = null; this.missed = 0; this.state = 'disconnected'; this.lastSeenAt = null;
      this.recoveryRun = null; this.recoveryAttempts = 0; this.recoveryDueAt = 0;
      this.pollGeneration = 0; this.waking = null; this.refreshOnResume = false;
      this.monitorOwner = null; this.monitorMembers = new Set([this]); this.monitorOrder = 0; this.handshakeOrder = 0;
      if (tracked) sessions.add(this);
    }
    transition(state, reason = null) {
      if (this.monitorOwner && this.monitorOwner !== this) return;
      const changed = this.state !== state;
      this.state = state;
      for (const member of this.monitorMembers) if (member !== this) {
        member.state = state; member.missed = this.missed; member.lastSeenAt = this.lastSeenAt;
      }
      if (changed) emit({ state, reason, serviceId: this.identity, lastSeenAt: this.lastSeenAt, missedCount: this.missed,
        printersStale: state !== 'online' });
    }
    stop() {
      eventMonitors.get(this)?.close(); eventMonitors.delete(this);
      this.disabled = true; this.lifetime.abort(); io.clearTimeout(this.timer); this.timer = null;
      this.connecting = null; this.polling = null;
      this.pollGeneration++; this.waking = null;
      this.recoveryRun?.cancel(); this.recoveryRun = null;
      this.transition('disconnected');
      this.monitorOwner = null; this.monitorMembers.clear();
    }
    async request(path, { wire, timeoutMs = this.config.requestTimeoutMs, authorized = true } = {}) {
      if (this.disabled) fail('NOT_CONNECTED', 'Call connect() before using a disconnected client.');
      const controller = new AbortController();
      const delivery = wire && (path === '/api/jobs' || /^\/api\/jobs\/[^/]+\/reprints$/.test(path)) ? 'unknown' : 'not_sent';
      let timeout;
      let rejectAbort;
      const aborted = new Promise((_, reject) => { rejectAbort = reject; });
      const cancel = () => {
        controller.abort();
        rejectAbort(new EntreePrintError('NOT_CONNECTED', 'The client was disconnected; reconcile any submitted print intent.', { delivery }));
      };
      this.lifetime.signal.addEventListener('abort', cancel, { once: true });
      timeout = io.setTimeout(() => {
        rejectAbort(new EntreePrintError('SERVICE_UNAVAILABLE', 'The print service did not respond before the deadline.', { delivery, retryable: true }));
        controller.abort();
      }, timeoutMs);
      const work = async () => {
        const headers = authorized ? { Authorization: `Bearer ${this.config.token}` } : {};
        if (this.identity) headers[SERVICE] = this.identity;
        if (wire) { headers[DIGEST] = wire.digest; headers['Content-Type'] = 'application/json'; }
        const response = await io.fetch(this.config.baseUrl + path, { method: wire ? 'POST' : 'GET',
          headers, body: wire?.bytes.slice(), signal: controller.signal, cache: 'no-store', credentials: 'omit', redirect: 'error' });
        let result;
        try { result = await response.json(); }
        catch { fail('RESPONSE_INVALID', 'The print service returned an unreadable response.', { delivery, retryable: true }); }
        if (!response.ok) {
          if (typeof result?.error?.code !== 'string') fail('RESPONSE_INVALID', 'The print service returned an unexpected error response.', { delivery, retryable: true });
          throw new EntreePrintError(result.error.code, result.error.message || 'The print service rejected the operation.',
            { retryable: result.error.retryable === true, delivery: result.error.delivery ?? delivery, details: result.error.details });
        }
        if (wire && response.headers.get(DIGEST)?.toLowerCase() !== wire.digest)
          fail('ACK_INVALID', 'The acknowledgement does not match the submitted bytes. Retry only this same print intent.', { delivery, retryable: true });
        if (result?.serviceId !== undefined && this.identity && result.serviceId !== this.identity)
          fail('SERVICE_MISMATCH', 'This address belongs to a different print service.', { delivery });
        return result;
      };
      try { return await Promise.race([work(), aborted]); }
      catch (error) {
        if (error instanceof EntreePrintError) throw error;
        fail('SERVICE_UNAVAILABLE', 'The print service could not be reached.', { delivery, retryable: true });
      }
      finally { io.clearTimeout(timeout); this.lifetime.signal.removeEventListener('abort', cancel); }
    }
    connect() {
      if (this.disabled) { this.lifetime = new AbortController(); this.disabled = false; sessions.add(this); }
      if (this.connecting) return this.connecting;
      const lifetime = this.lifetime;
      this.connecting = (async () => {
        const info = await this.handshake();
        if (this.disabled || lifetime !== this.lifetime) fail('NOT_CONNECTED', 'The connection attempt is no longer active.');
        this.adopt(info);
        return snapshot(this.info);
      })();
      const pending = this.connecting;
      pending.finally(() => { if (this.connecting === pending) this.connecting = null; }).catch(() => {});
      return pending;
    }
    async handshake(timeoutMs = this.config.requestTimeoutMs) {
      this.handshakeOrder = ++handshakeSequence;
      if (this.config.token.length < 32) fail('AUTH_NOT_CONFIGURED', 'Configure the API token from the print-service settings.');
      const info = await this.request('/api/connection', { timeoutMs });
      if (!info || typeof info.serviceId !== 'string' || !info.serviceId || typeof info.bootId !== 'string' || !info.bootId ||
        info.apiVersion !== '0.0.1' || !Array.isArray(info.printers) || info.printers.some(printer => typeof printer?.name !== 'string' || !printer.name))
        fail('PROTOCOL_UNSUPPORTED', 'The endpoint did not return a compatible 0.0.1 connection.');
      if (!info.printers.length) fail('NO_PRINTERS', 'This service has no installed printers.', { details: { serviceId: info.serviceId } });
      return { ...info, ip: this.config.ip, port: this.config.port };
    }
    adopt(info, order = this.handshakeOrder) {
      this.identity = info.serviceId;
      // Credentials and monitoring deadlines are part of the trust/policy boundary.
      const key = JSON.stringify([this.identity, this.config.token, this.config.requestTimeoutMs, canonical(this.config.heartbeat)]);
      let owner = monitors.get(key);
      if (!owner || owner.disabled) { owner = this; monitors.set(key, owner); }
      owner.monitorMembers.add(this); this.monitorOwner = owner;
      io.clearTimeout(this.timer); this.timer = null;
      if (order >= owner.monitorOrder) {
        owner.monitorOrder = order; owner.pollGeneration++;
        const endpoint = { ip: this.config.ip, port: this.config.port, protocol: this.config.protocol, baseUrl: this.config.baseUrl };
        for (const member of owner.monitorMembers) {
          member.config = { ...member.config, ...endpoint }; member.info = snapshot(info);
          if (member === current) configuration = member.config;
        }
        owner.missed = 0; owner.recoveryAttempts = 0; owner.recoveryDueAt = 0;
        owner.refreshOnResume = false;
        owner.lastSeenAt = new Date(io.now()).toISOString(); owner.transition('online');
      } else {
        // A slower, older handshake must not restore an obsolete address or clear a newer failure.
        this.config = { ...this.config, ip: owner.config.ip, port: owner.config.port,
          protocol: owner.config.protocol, baseUrl: owner.config.baseUrl };
        this.info = snapshot(owner.info); this.state = owner.state; this.missed = owner.missed; this.lastSeenAt = owner.lastSeenAt;
        if (this === current) configuration = this.config;
      }
      owner.schedule();
      watchResume();
      updateEventMonitors();
    }
    async ready() {
      if (this.disabled) fail('NOT_CONNECTED', 'Call connect() before using a disconnected client.');
      if (!this.info) await this.connect();
    }
    schedule() {
      io.clearTimeout(this.timer);
      if (this.disabled || (this.monitorOwner && this.monitorOwner !== this)) return;
      const interval = this.state === 'offline' && io.discover
        ? Math.min(this.config.heartbeat.intervalMs, Math.max(1, this.recoveryDueAt - io.now())) : this.config.heartbeat.intervalMs;
      this.timer = io.setTimeout(() => { this.poll().catch(() => {}); }, interval);
      this.timer?.unref?.();
    }
    poll() {
      if (this.disabled || !this.info) return Promise.resolve();
      if (this.monitorOwner && this.monitorOwner !== this) return this.monitorOwner.poll();
      if (this.polling) return this.polling;
      io.clearTimeout(this.timer);
      const lifetime = this.lifetime;
      const generation = this.pollGeneration;
      this.polling = (async () => {
        try {
          if (this.state === 'offline' && io.discover && io.now() >= this.recoveryDueAt) {
            await this.recover(lifetime);
            return;
          }
          const requestId = randomKey();
          const result = await this.request(`/api/heartbeat?requestId=${requestId}`, { timeoutMs: this.config.heartbeat.timeoutMs });
          if (result?.requestId !== requestId || result.serviceId !== this.identity || typeof result.bootId !== 'string')
            fail('HEARTBEAT_INVALID', 'The heartbeat did not match the current service and request.');
          if (this.disabled || lifetime !== this.lifetime || generation !== this.pollGeneration) return;
          if (this.missed || this.refreshOnResume || result.bootId !== this.info.bootId) {
            this.transition('reconnecting');
            await this.connect(); // Revalidate identity and refresh inventory before reporting recovery.
          } else {
            this.lastSeenAt = new Date(io.now()).toISOString(); this.missed = 0; this.transition('online');
          }
        } catch (error) {
          if (this.disabled || lifetime !== this.lifetime || generation !== this.pollGeneration) return;
          this.missed++;
          this.transition(this.missed >= this.config.heartbeat.missedLimit ? 'offline' : 'degraded', error.code ?? 'SERVICE_UNAVAILABLE');
          if (this.state === 'offline' && !this.recoveryDueAt) this.recoveryDueAt = io.now() + 1000;
        }
      })();
      const pending = this.polling;
      pending.finally(() => {
        if (this.polling === pending) this.polling = null;
        if (lifetime === this.lifetime) this.schedule();
      }).catch(() => {});
      return pending;
    }
    wake() {
      if (this.disabled || !this.info) return Promise.resolve();
      if (this.monitorOwner && this.monitorOwner !== this) return this.monitorOwner.wake();
      if (this.waking) return this.waking;
      const lifetime = this.lifetime;
      this.refreshOnResume = true;
      this.pollGeneration++;
      if (this.state === 'online') this.transition('degraded', 'POS_RESUMED');
      const previous = this.polling;
      const pending = (async () => {
        if (previous) await previous;
        if (!this.disabled && lifetime === this.lifetime) await this.poll();
      })();
      this.waking = pending;
      pending.finally(() => { if (this.waking === pending) this.waking = null; }).catch(() => {});
      return pending;
    }
    async recover(lifetime) {
      this.transition('reconnecting');
      const run = new DiscoveryRun(this.config, { timeoutMs: 3000, serviceId: this.identity });
      this.recoveryRun = run;
      try {
        const winner = await run.first();
        if (this.disabled || lifetime !== this.lifetime) return;
        if (winner.info.serviceId !== this.identity) fail('SERVICE_MISMATCH', 'Discovery found another service.');
        this.config = winner.session.config;
        if (current === this) configuration = this.config;
        this.adopt(winner.info, winner.session.handshakeOrder);
      } catch (error) {
        if (this.disabled || lifetime !== this.lifetime) return;
        this.recoveryAttempts++;
        const jitter = 0.8 + (io.crypto.getRandomValues(new Uint8Array(1))[0] / 255) * 0.4;
        this.recoveryDueAt = io.now() + Math.min(30000, 1000 * 2 ** Math.min(5, this.recoveryAttempts) * jitter);
        this.transition('offline', error.code ?? 'DISCOVERY_FAILED');
      } finally {
        run.cancel();
        if (this.recoveryRun === run) this.recoveryRun = null;
      }
    }
    async read(path) { await this.ready(); return this.request(path); }
    async submit(wire, key, path = '/api/jobs', reprintOf) {
      if (this.nodeSelection?.usedBackup) {
        if (reprintOf !== undefined)
          fail('JOB_OWNER_REQUIRED', 'Connect directly to the original job owner to request a reprint.');
        const body = JSON.parse(new TextDecoder().decode(wire.bytes));
        verifyBackupDestination(this, body.printer, body.type !== 'print' || !!trailingOptions(body.after));
      }
      await this.ready();
      for (let attempt = 0; ; attempt++) {
        try {
          const result = await this.request(path, { wire });
          if (result?.serviceId !== this.identity || result.idempotencyKey !== key || typeof result.id !== 'string' || result.integrity?.verified !== true
              || (reprintOf !== undefined && result.reprintOf !== reprintOf))
            fail('ACK_INVALID', 'The acknowledgement does not identify this service and print intent.', { delivery: 'unknown', retryable: true });
          return result;
        } catch (error) {
          error.details = { ...error.details, serviceId: this.identity, idempotencyKey: key };
          if (!error.retryable || attempt >= this.config.retryCount || this.disabled) throw error;
          await new Promise((resolve, reject) => {
            const signal = this.lifetime.signal;
            const cancel = () => { io.clearTimeout(timer); reject(new EntreePrintError('NOT_CONNECTED', 'The client was disconnected.',
              { delivery: 'unknown', details: { serviceId: this.identity, idempotencyKey: key } })); };
            const timer = io.setTimeout(() => { signal.removeEventListener('abort', cancel); resolve(); }, this.config.retryDelayMs);
            signal.addEventListener('abort', cancel, { once: true });
            if (signal.aborted) cancel();
          });
        }
      }
    }
  }

  function sessionFor(config, expectedIdentity = null) {
    const matching = [...sessions].find(session => !session.disabled && !session.nodeSelection &&
      (!expectedIdentity || session.identity === expectedIdentity) &&
      JSON.stringify(canonical(session.config)) === JSON.stringify(canonical(config)));
    if (matching) return matching;
    const session = new Session(config); session.identity = expectedIdentity; return session;
  }

  // One bounded discovery window can serve both await search() and search().connect().
  // Unselected probes never register heartbeat timers or become a default destination.
  class DiscoveryRun {
    constructor(config, options, cached = null) {
      this.config = config; this.options = options; this.cached = cached;
      this.controller = new AbortController(); this.records = new Map(); this.probes = new Set();
      this.endpoints = new Set(); this.queue = []; this.active = 0; this.sourceDone = false;
      this.started = false; this.completed = false; this.wantsConnection = false; this.winner = null; this.errors = [];
      this.all = new Promise((resolve, reject) => { this.resolveAll = resolve; this.rejectAll = reject; });
      this.connected = new Promise((resolve, reject) => { this.resolveFirst = resolve; this.rejectFirst = reject; });
      this.all.catch(() => {}); this.connected.catch(() => {});
    }
    start() {
      if (this.started) return;
      this.started = true; discoveries.add(this);
      if (!io.discover && !this.cached) { this.finish(new EntreePrintError('DISCOVERY_UNAVAILABLE', 'Use the native discovery adapter or configure the service address.')); return; }
      this.timer = io.setTimeout(() => this.finish(), this.options.timeoutMs);
      if (this.cached) {
        for (const candidate of this.cached) this.candidate(candidate);
        this.sourceDone = true; this.pump();
      } else {
        Promise.resolve().then(() => this.completed ? undefined : io.discover({ timeoutMs: this.options.timeoutMs, signal: this.controller.signal,
          serviceId: this.options.serviceId, onCandidate: candidate => this.candidate(candidate) }))
          .then(() => { this.sourceDone = true; this.pump(); }, error => {
            if (!this.completed) this.finish(new EntreePrintError('DISCOVERY_FAILED', 'The discovery transport failed.', { details: { code: error.code ?? 'TRANSPORT_ERROR' } }));
          });
      }
    }
    first() {
      this.wantsConnection = true;
      for (const probe of this.probes) if (probe.record) this.tryConnection(probe);
      this.start(); this.pump(); return this.connected;
    }
    collect() { this.start(); return this.all.then(snapshot); }
    candidate(candidate) {
      if (this.completed || this.endpoints.size >= 64) return;
      try {
        if (!candidate || (this.options.serviceId && candidate.serviceId && candidate.serviceId !== this.options.serviceId)) return;
        const config = settings(this.config, { ip: candidate.ip, port: candidate.port,
          protocol: candidate.protocol ?? this.config.protocol });
        if (this.endpoints.has(config.baseUrl)) return;
        this.endpoints.add(config.baseUrl);
        const probe = { session: new Session(config, false), candidate, record: null, checking: false };
        this.probes.add(probe);
        this.queue.push(async () => {
          try {
            const requestId = randomKey();
            const health = await probe.session.request(`/api/health?requestId=${requestId}`, { authorized: false });
            if (health?.service !== 'entree-print-plugin' || health.apiVersion !== '0.0.1' || health.requestId !== requestId ||
              typeof health.serviceId !== 'string' || !health.serviceId || typeof health.bootId !== 'string' || !health.bootId)
              fail('DISCOVERY_RESPONSE_INVALID', 'The endpoint failed discovery verification.');
            const expected = this.options.serviceId ?? candidate.serviceId;
            if (expected && expected !== health.serviceId) fail('SERVICE_MISMATCH', 'The discovery identity does not match the endpoint.');
            if (this.completed) return;
            probe.session.identity = health.serviceId;
            probe.record = { serviceId: health.serviceId, ip: config.ip, port: config.port, protocol: config.protocol,
              name: typeof candidate.name === 'string' ? candidate.name.slice(0, 128) : 'ENTREE Print' };
            if (!this.records.has(health.serviceId)) this.records.set(health.serviceId, probe.record);
            if (this.wantsConnection) this.tryConnection(probe);
          } catch (error) { this.failure(probe, error); }
        });
        this.pump();
      } catch { /* Invalid transport candidates never become HTTP requests. */ }
    }
    tryConnection(probe) {
      if (probe.checking || this.winner || this.completed) return;
      probe.checking = true;
      // Prioritize handshakes for verified endpoints over additional health probes.
      this.queue.unshift(async () => {
        if (this.completed || this.winner) return;
        try {
          const info = await probe.session.handshake();
          if (this.completed || this.winner) return;
          this.winner = { session: probe.session, info };
          this.resolveFirst(this.winner);
        } catch (error) { this.failure(probe, error); }
      });
      this.pump();
    }
    failure(probe, error) {
      if (!this.completed) this.errors.push({ ip: probe.session.config.ip, port: probe.session.config.port, code: error.code ?? 'SERVICE_UNAVAILABLE' });
    }
    pump() {
      if (this.completed) return;
      while (this.active < 4 && this.queue.length) {
        const operation = this.queue.shift(); this.active++;
        Promise.resolve().then(operation).finally(() => { this.active--; this.pump(); }).catch(() => {});
      }
      if (this.sourceDone && this.active === 0 && this.queue.length === 0) this.finish();
    }
    finish(error = null) {
      if (this.completed) return;
      this.completed = true; io.clearTimeout(this.timer); this.controller.abort(); discoveries.delete(this);
      this.queue = [];
      for (const probe of this.probes) if (probe.session !== this.winner?.session) probe.session.stop();
      if (error) this.rejectAll(error); else this.resolveAll([...this.records.values()]);
      if (!this.winner) this.rejectFirst(error ?? new EntreePrintError(this.endpoints.size ? 'CONNECTION_FAILED' : 'SERVICE_NOT_FOUND',
        this.endpoints.size ? 'No discovered service completed the connection handshake.' : 'No print service was found.', { details: { candidates: this.errors } }));
    }
    cancel() {
      this.finish(new EntreePrintError('NOT_CONNECTED', 'Discovery was cancelled.'));
      if (this.winner && !sessions.has(this.winner.session)) this.winner.session.stop();
    }
  }

  function search(options = {}) {
    fields(options, ['timeoutMs', 'serviceId']);
    const request = { timeoutMs: number(options.timeoutMs ?? 3000, 'timeoutMs', 1, 30000),
      serviceId: options.serviceId === undefined ? undefined : text(options.serviceId, 'serviceId', 128) };
    const config = configuration;
    let run = new DiscoveryRun(config, request);
    let selected = null;
    let collection = null;
    const collect = () => collection ??= run.collect();
    return Object.freeze({
      then(resolve, reject) { return collect().then(snapshot).then(resolve, reject); },
      catch(reject) { return collect().then(snapshot).catch(reject); },
      finally(handler) { return collect().then(snapshot).finally(handler); },
      connect() {
        if (!selected) {
          const revision = ++selection;
          if (run.completed) run = new DiscoveryRun(config, request, [...run.records.values()]);
          selected = run.first().then(winner => {
            if (revision !== selection || winner.session.disabled) {
              winner.session.stop(); fail('CONNECTION_SUPERSEDED', 'A newer connection selection replaced this search.');
            }
            current = winner.session; configuration = winner.session.config; sessions.add(current); current.adopt(winner.info);
            return { session: current, info: current.info };
          });
        }
        return selected.then(result => {
          if (result.session.disabled) fail('NOT_CONNECTED', 'Call connect() again on the client after disconnecting.');
          return connectedLibrary(result.session);
        });
      }
    });
  }

  function content(value) {
    if (typeof value === 'string') return text(value, 'HTML', 250000);
    if (!Array.isArray(value) || !value.length || value.length > 200) fail('CONTENT_INVALID', 'Use HTML or an array of 1–200 content blocks.');
    const copied = snapshot(value);
    for (const block of copied) if (!['html', 'qrcode', 'barcode'].includes(block?.type))
      fail('CONTENT_TYPE_UNSUPPORTED', 'Use html, qrcode or barcode blocks. Image input is unsupported.');
    return copied;
  }
  function trailingOptions(value) {
    if (value === undefined) return undefined;
    fields(value, ['cut', 'beep']);
    if (Object.values(value).some(flag => typeof flag !== 'boolean')) fail('COMMAND_INVALID', 'Trailing action flags must be booleans.');
    const result = Object.fromEntries(['cut','beep'].filter(name => value[name] === true).map(name => [name,true]));
    return Object.keys(result).length ? result : undefined;
  }
  function printOptions(options, receipt = false) {
    fields(options, ['idempotencyKey', 'metadata', ...(receipt ? ['after'] : [])]);
    if (options.idempotencyKey !== undefined) text(options.idempotencyKey, 'idempotencyKey');
    if (options.metadata !== undefined && (!options.metadata || typeof options.metadata !== 'object' || Array.isArray(options.metadata)))
      fail('METADATA_INVALID', 'metadata must be an object.');
    const copy = snapshot(options);
    if (receipt) { delete copy.after; const after = trailingOptions(options.after); if (after) copy.after = after; }
    return copy;
  }

  function verifyBackupDestination(session, printerName, deviceCommand = false) {
    if (!session.nodeSelection?.usedBackup) return;
    if (deviceCommand) fail('DEVICE_OWNER_REQUIRED', 'Connect directly to the device owner for drawer, beep, cut or raw commands.');
    const printer = session.info?.printers.find(item => item.name === printerName);
    if (printer?.connection?.type !== 'network' || typeof printer.connection.host !== 'string' || !printer.connection.host)
      fail('PRINTER_OWNER_REQUIRED', 'Backup connections can print only to identified network queues. Connect directly to the USB or unknown printer owner.');
  }

  class Ticket {
    #session; #printer; #content; #width; #rendering = null; #defaultKey = null; #intents = new Map();
    constructor(session, printer, value, width) { this.#session = session; this.#printer = printer; this.#content = value; this.#width = width; }
    setContent(value) { return new Ticket(this.#session, this.#printer, content(value), this.#width); }
    setWidth(mm) { return new Ticket(this.#session, this.#printer, this.#content, number(mm, 'width', 20, 100)); }
    async render() {
      await this.#session.ready();
      if (this.#rendering) {
        const original = this.#rendering;
        const cached = await original;
        if (Date.parse(cached.expiresAt) > io.now()) return snapshot(cached);
        if (this.#rendering === original) this.#rendering = null; // Explicit render() requests a fresh preview.
      }
      if (!this.#rendering) {
        const pending = (async () => {
          const body = { printer: this.#printer, ...(typeof this.#content === 'string' ? { html: this.#content } : { content: this.#content }),
            ...(this.#width === undefined ? {} : { widthMm: this.#width }) };
          const result = await this.#session.request('/api/renders', { wire: await encode(body), timeoutMs: this.#session.config.renderTimeoutMs });
          if (result?.serviceId !== this.#session.identity || typeof result.id !== 'string' || typeof result.html !== 'string' || !Number.isFinite(Date.parse(result.expiresAt)))
            fail('RESPONSE_INVALID', 'The service returned an invalid prepared receipt.');
          return snapshot(result);
        })();
        this.#rendering = pending;
        pending.catch(() => { if (this.#rendering === pending) this.#rendering = null; });
      }
      return snapshot(await this.#rendering);
    }
    async print(options = {}) {
      const supplied = printOptions(options, true);
      verifyBackupDestination(this.#session, this.#printer, !!supplied.after);
      const key = supplied.idempotencyKey ?? (this.#defaultKey ??= randomKey());
      const metadata = JSON.stringify(canonical(supplied.metadata ?? {}));
      const signature = JSON.stringify({ metadata, after:supplied.after });
      let intent = this.#intents.get(key);
      if (intent && intent.signature !== signature) fail('IDEMPOTENCY_CONFLICT', 'This ticket already uses the key with different metadata or trailing actions.');
      if (!intent) {
        // Capture the preparation promise and bytes once. Later render() calls cannot change a retry.
        const rendering = this.#rendering ?? this.render();
        intent = { signature, wire: rendering.then(result => encode({ type: 'print', printer: this.#printer,
          renderId: result.id, idempotencyKey: key, metadata: JSON.parse(metadata), ...(supplied.after ? {after:supplied.after} : {}) })), inFlight: null };
        this.#intents.set(key, intent);
        const captured = intent;
        intent.wire.catch(() => { if (this.#intents.get(key) === captured) this.#intents.delete(key); });
      }
      if (!intent.inFlight) {
        const captured = intent;
        const pending = intent.wire.then(wire => this.#session.submit(wire, key)).catch(error => {
          // Only this authoritative rejection allows the operator to prepare a new preview for the same intent.
          // An unknown ACK/transport outcome always retains the original wire bytes.
          if (error.code === 'RENDER_EXPIRED' && error.delivery === 'not_sent' && this.#intents.get(key) === captured)
            this.#intents.delete(key);
          throw error;
        });
        intent.inFlight = pending;
        pending.finally(() => { if (intent.inFlight === pending) intent.inFlight = null; }).catch(() => {});
      }
      return snapshot(await intent.inFlight);
    }
  }

  class PrinterTarget {
    #session; #name; #width;
    constructor(session, name, width) { this.#session = session; this.#name = text(name, 'printer name'); this.#width = width; }
    setWidth(mm) { return new PrinterTarget(this.#session, this.#name, number(mm, 'width', 20, 100)); }
    setContent(value) { return new Ticket(this.#session, this.#name, content(value), this.#width); }
    status(options = {}) { fields(options, ['refresh']); return this.#session.read(`/api/printers/status?printer=${encodeURIComponent(this.#name)}&refresh=${options.refresh === true}`); }
    async #command(type, options, bytesBase64) {
      verifyBackupDestination(this.#session, this.#name, true);
      const supplied = printOptions(options);
      const key = supplied.idempotencyKey ?? randomKey();
      const body = { type, printer: this.#name, idempotencyKey: key, ...(supplied.metadata ? { metadata: supplied.metadata } : {}),
        ...(bytesBase64 === undefined ? {} : { bytesBase64 }) };
      return this.#session.submit(await encode(body), key);
    }
    beep(options = {}) { return this.#command('beep', options); }
    openDrawer(options = {}) { return this.#command('open_cash_drawer', options); }
    cut(options = {}) { return this.#command('cut', options); }
    sendCommand(bytes, options = {}) {
      if (!(bytes instanceof Uint8Array) || bytes.length < 1 || bytes.length > 4096) fail('COMMAND_INVALID', 'sendCommand requires a Uint8Array of 1–4096 bytes.');
      return this.#command('send_command', options, btoa(String.fromCharCode(...bytes)));
    }
  }

  function printingOperations(getSession) { return {
    target(name) { return new PrinterTarget(getSession(), name); },
    getPrinters(options = {}) { fields(options, ['refresh']); return getSession().read(`/api/printers?refresh=${options.refresh === true}`); },
    getJobs(printerName, filters = {}) {
      fields(filters, ['station', 'orderID', 'status', 'since', 'limit', 'cursor']);
      const query = new URLSearchParams();
      if (printerName !== undefined) query.set('printer', text(printerName, 'printer name'));
      for (const field of ['station', 'orderID']) if (filters[field] !== undefined) query.set(field, text(filters[field], field, 256));
      if (filters.cursor !== undefined) query.set('cursor', text(filters.cursor, 'cursor', 2048));
      if (filters.limit !== undefined) {
        if (!Number.isInteger(filters.limit)) fail('REQUEST_INVALID', 'limit must be an integer.');
        query.set('limit', number(filters.limit, 'limit', 1, 100));
      }
      if (filters.since !== undefined) {
        const since = text(filters.since, 'since', 64);
        if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$/.test(since) || !Number.isFinite(Date.parse(since)))
          fail('REQUEST_INVALID', 'since must be an ISO date/time including Z or a time-zone offset.');
        query.set('since', since);
      }
      if (filters.status !== undefined) {
        const states = ['accepted', 'waiting_for_printer', 'submitting', 'submitted', 'printing', 'blocked', 'completed', 'failed', 'needs_attention'];
        const supplied = Array.isArray(filters.status) ? filters.status : [filters.status];
        if (!supplied.length || supplied.length > states.length || supplied.some(state => !states.includes(state)))
          fail('REQUEST_INVALID', 'status must contain supported job states.');
        for (const state of new Set(supplied)) query.append('status', state);
      }
      return getSession().read(`/api/jobs?${query}`);
    },
    async reprintJob(id, options = {}) {
      fields(options, ['idempotencyKey']);
      const original = text(id, 'job ID');
      const key = text(options.idempotencyKey, 'idempotencyKey');
      const session = getSession();
      return session.submit(await encode({ idempotencyKey: key }), key, `/api/jobs/${encodeURIComponent(original)}/reprints`, original);
    },
    getJob(id) {
      const session = getSession();
      if (typeof id === 'string') return session.read(`/api/jobs/${encodeURIComponent(text(id, 'job ID'))}`);
      fields(id, ['idempotencyKey']);
      const key = text(id.idempotencyKey, 'idempotencyKey');
      if (!key.trim()) fail('REQUEST_INVALID', 'idempotencyKey cannot be blank.');
      return session.read(`/api/jobs/lookup?${new URLSearchParams({ idempotencyKey: key })}`).then(result => {
        if (result?.serviceId !== session.identity || result.idempotencyKey !== key || typeof result.id !== 'string' || !result.id)
          fail('RESPONSE_INVALID', 'The lookup response does not identify this service and print intent.', { delivery: 'unknown' });
        return result;
      }).catch(error => {
        // A lookup failure, including not-found, is not proof about an earlier submission's delivery.
        error.delivery = 'unknown';
        error.details = { ...error.details, serviceId: session.identity, idempotencyKey: key, operation: 'lookup' };
        throw error;
      });
    },
    getJobRender(id) { return getSession().read(`/api/jobs/${encodeURIComponent(text(id, 'job ID'))}/render`); }
  }; }

  function connectedLibrary(session) {
    return session.library ??= Object.freeze({
      version: '0.0.1-beta',
      get connection() {
        const { printers, ...info } = session.info;
        return snapshot({ ...info, ...(session.nodeSelection ? { nodeSelection: session.nodeSelection } : {}) });
      },
      ...printingOperations(() => session)
    });
  }

  function connectNodes(options) {
    fields(options, ['nodes', 'failover', 'timeoutMs']);
    if (!Array.isArray(options.nodes) || options.nodes.length < 1 || options.nodes.length > 8)
      fail('REQUEST_INVALID', 'Provide 1–8 configured print servers.');
    if (options.failover !== undefined && typeof options.failover !== 'boolean')
      fail('REQUEST_INVALID', 'failover must be a boolean.');
    const timeoutMs = number(options.timeoutMs ?? 10000, 'timeoutMs', 1, 120000);
    const identities = new Set(), addresses = new Set();
    // Validate and copy every candidate before contacting any server. A node's
    // credential must be explicit; never inherit another server's token.
    const nodes = options.nodes.map(node => {
      fields(node, [...Object.keys(defaults), 'serviceId', 'name']);
      const { serviceId, name, ...connection } = node;
      const identity = text(serviceId, 'serviceId', 128);
      text(connection.ip, 'ip', 253);
      if (typeof connection.token !== 'string' || connection.token.length < 32)
        fail('AUTH_NOT_CONFIGURED', 'Each configured server requires its own API token.');
      if (name !== undefined) text(name, 'name', 128);
      const config = settings({ ...configuration, token: '' }, snapshot(connection));
      if (identities.has(identity) || addresses.has(config.baseUrl))
        fail('REQUEST_INVALID', 'Configured servers must have distinct identities and addresses.');
      identities.add(identity); addresses.add(config.baseUrl);
      return { identity, config };
    });
    const generation = ++selection;
    const count = options.failover === false ? 1 : nodes.length;
    const attempts = nodes.map(node => ({ serviceId: node.identity, ip: node.config.ip, port: node.config.port,
      protocol: node.config.protocol, state: 'not_checked' }));
    return (async () => {
      const deadline = io.now() + timeoutMs;
      let active = null, expired = false;
      const timer = io.setTimeout(() => { expired = true; active?.stop(); }, timeoutMs);
      try {
        for (let index = 0; index < count; index++) {
          if (generation !== selection) fail('CONNECTION_SUPERSEDED', 'A newer connection selection replaced this attempt.');
          const remaining = deadline - io.now();
          if (expired || remaining <= 0) break;
          const node = nodes[index];
          active = new Session(node.config); active.identity = node.identity;
          try {
            // Reserve time for later candidates if an earlier address is silent.
            const info = await active.handshake(Math.min(node.config.requestTimeoutMs, Math.max(1, remaining / (count - index))));
            if (generation !== selection) fail('CONNECTION_SUPERSEDED', 'A newer connection selection replaced this attempt.');
            if (expired || io.now() >= deadline) fail('SERVICE_UNAVAILABLE', 'Configured server selection timed out.');
            attempts[index].state = 'selected';
            active.nodeSelection = snapshot({ selectedServiceId: node.identity, usedBackup: index > 0, nodes: attempts });
            current = active; configuration = active.config; active.adopt(info);
            if (generation !== selection || active.disabled)
              fail('CONNECTION_SUPERSEDED', 'A newer connection selection replaced this attempt.');
            return connectedLibrary(active);
          } catch (error) {
            active.stop(); sessions.delete(active);
            attempts[index].state = 'failed'; attempts[index].code = expired ? 'SERVICE_UNAVAILABLE' : error.code || 'SERVICE_UNAVAILABLE';
            if (generation !== selection) fail('CONNECTION_SUPERSEDED', 'A newer connection selection replaced this attempt.');
          }
        }
        fail('SERVICE_UNAVAILABLE', 'None of the configured print servers could be connected.', { retryable: true, details: { nodes: snapshot(attempts) } });
      } finally { io.clearTimeout(timer); }
    })();
  }

  current = new Session(configuration);
  const api = {
    version: '0.0.1-beta',
    config(options) { configuration = settings(configuration, options); current = sessionFor(configuration); selection++; return api; },
    connect(options) {
      if (options && Object.hasOwn(options, 'nodes')) return connectNodes(options);
      if (options !== undefined) {
        // Accept a discovery record directly as well as ordinary connection settings.
        fields(options, [...Object.keys(defaults), 'serviceId', 'name']);
        const { serviceId, name, ...connection } = options;
        const expected = serviceId === undefined ? null : text(serviceId, 'serviceId', 128);
        const configured = settings(configuration, connection);
        configuration = configured;
        current = sessionFor(configured, expected);
      }
      selection++;
      const session = current;
      return session.connect().then(() => connectedLibrary(session));
    },
    search,
    outbox() {
      const ownerSession = library => {
        const session = [...sessions].find(item => item.library === library && !item.disabled);
        if (!session?.identity) fail('NOT_CONNECTED', 'Pass a connected library from this SDK to flush its outbox.');
        return session;
      };
      return outbox ??= createOutboxController(io.outboxStorage ?? createIndexedDbOutboxStorage(), {
        owner: library => ownerSession(library).identity,
        validate(value, stored = false) {
          if (!stored) fields(value, ['idempotencyKey', 'serviceId', 'printer', 'content', 'widthMm', 'metadata', 'after']);
          const source = { idempotencyKey: text(value.idempotencyKey, 'idempotencyKey'), serviceId: text(value.serviceId, 'serviceId', 128),
            printer: text(value.printer, 'printer name'), content: content(value.content) };
          if (!source.idempotencyKey.trim()) fail('REQUEST_INVALID', 'idempotencyKey cannot be blank.');
          if (value.widthMm !== undefined) source.widthMm = number(value.widthMm, 'width', 20, 100);
          if (value.metadata !== undefined) source.metadata = printOptions({ metadata: value.metadata }).metadata;
          const after = trailingOptions(value.after); if (after) source.after = after;
          return source;
        },
        fingerprint: async source => (await encode(canonical(source))).digest,
        async prepare(library, record) {
          const session = ownerSession(library);
          verifyBackupDestination(session, record.printer, !!record.after);
          const ticket = new Ticket(session, record.printer, record.content, record.widthMm);
          const rendered = await ticket.render();
          const body = { type: 'print', printer: record.printer, renderId: rendered.id, idempotencyKey: record.idempotencyKey,
            ...(record.metadata === undefined ? {} : { metadata: record.metadata }), ...(record.after ? {after:record.after} : {}) };
          const wire = await encode(body);
          return { json: new TextDecoder().decode(wire.bytes), digest: wire.digest };
        },
        async submit(library, record) {
          const session = ownerSession(library);
          if (session.identity !== record.serviceId) fail('SERVICE_MISMATCH', 'The saved intent belongs to another service.');
          let body;
          try { body = JSON.parse(record.wire.json); } catch { fail('OUTBOX_CORRUPT', 'The saved print request is invalid.'); }
          fields(body, ['type', 'printer', 'renderId', 'idempotencyKey', 'metadata', 'after']);
          const encoded = await encode(body);
          if (body.type !== 'print' || body.printer !== record.printer || body.idempotencyKey !== record.idempotencyKey ||
            typeof body.renderId !== 'string' || !body.renderId || encoded.digest !== record.wire.digest ||
            new TextDecoder().decode(encoded.bytes) !== record.wire.json ||
            JSON.stringify(canonical(body.metadata)) !== JSON.stringify(canonical(record.metadata)) ||
            JSON.stringify(canonical(body.after)) !== JSON.stringify(canonical(record.after)))
            fail('OUTBOX_CORRUPT', 'The saved print request failed its identity or content check.');
          return session.submit(encoded, record.idempotencyKey);
        }
      });
    },
    disconnect() {
      selection++; unsubscribeResume?.(); unsubscribeResume = null;
      for (const run of discoveries) run.cancel(); for (const session of sessions) session.stop();
      monitors.clear(); sessions.clear();
    },
    ...printingOperations(() => current),
    subscribe(handler, options = {}) {
      fields(options, ['events']);
      if (typeof handler !== 'function') fail('REQUEST_INVALID', 'subscribe requires a callback.');
      const events = options.events ?? ['connection','printer','job','sync'];
      if (!Array.isArray(events) || !events.length || events.some(event => !['connection','printer','job','sync'].includes(event)))
        fail('EVENT_UNSUPPORTED', 'Use connection, printer, job or sync events.');
      const listener = { handler, events:[...events], remote:events.some(event => event !== 'connection') };
      listeners.add(listener); updateEventMonitors(listener.remote);
      return { close() { listeners.delete(listener); updateEventMonitors(); } };
    },
    // Call from Electron resume or a browser visibility handler; this never submits a print job.
    resume() { return Promise.all([...sessions].map(session => session.poll())); }
  };
  return Object.freeze(api);
}

export const EntreePrint = createEntreePrint();
export default EntreePrint;
