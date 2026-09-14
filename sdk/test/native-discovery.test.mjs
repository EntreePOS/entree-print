import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { createNativeDiscovery } from '../native-discovery.mjs';
import { Clock } from './helpers.mjs';

class Socket extends EventEmitter {
  packets = []; closes = 0; bound = false; failSend = false;
  bind(port, address) { this.binding = { port, address }; }
  listening() { this.bound = true; this.emit('listening'); }
  setBroadcast(value) { this.broadcast = value; }
  send(bytes, port, address, callback) { this.packets.push({ bytes: bytes.toString(), port, address }); callback(this.failSend ? new Error('send failed') : null); }
  close() { this.closes++; }
}
const packet = (overrides = {}) => Buffer.from(JSON.stringify({ ok: true, service: 'entree-print-plugin', protocol: 'entree-print',
  apiVersion: '0.0.1', serviceId: 'service-id', port: 9779, name: 'Kitchen computer', addresses: ['untrusted-advertised-host'], ...overrides }));

function fixture() {
  const clock = new Clock(); const socket = new Socket(); const controller = new AbortController(); const candidates = [];
  let created = 0;
  const discover = createNativeDiscovery({ port: 18778 }, { createSocket: () => { created++; return socket; },
    networkInterfaces: () => ({ Ethernet: [{ internal: false, family: 'IPv4', address: '192.168.20.8', netmask: '255.255.255.0' }],
      Loopback: [{ internal: true, family: 'IPv4', address: '127.0.0.1', netmask: '255.0.0.0' }] }),
    setTimeout: clock.setTimeout, clearTimeout: clock.clearTimeout });
  return { clock, socket, controller, candidates, discover, created: () => created };
}

test('native discovery creates one socket lazily, broadcasts on configured port, and cleans up on deadline', async () => {
  const { clock, socket, controller, candidates, discover, created } = fixture();
  assert.equal(created(), 0);
  const done = discover({ signal: controller.signal, timeoutMs: 100, onCandidate: value => candidates.push(value) });
  assert.equal(created(), 1); assert.deepEqual(socket.binding, { port: 0, address: '0.0.0.0' });
  socket.listening();
  assert.equal(socket.broadcast, true);
  assert.deepEqual(socket.packets.map(item => item.address), ['127.0.0.1', '255.255.255.255', '192.168.20.255']);
  assert.ok(socket.packets.every(item => item.port === 18778 && item.bytes === 'ENTREE_PRINT_DISCOVER'));
  await clock.advance(100); await done;
  assert.equal(socket.closes, 1); assert.equal(socket.listenerCount('message'), 0); assert.equal(clock.timers.size, 0);
});

test('only compatible datagrams from the discovery port become candidates; advertised addresses are ignored', async () => {
  const { socket, controller, candidates, discover } = fixture();
  const done = discover({ signal: controller.signal, onCandidate: value => candidates.push(value), serviceId: 'service-id' });
  socket.listening(); const sender = { address: '192.168.20.40', port: 18778 };
  socket.emit('message', Buffer.from('invalid'), sender);
  socket.emit('message', Buffer.alloc(9000), sender);
  socket.emit('message', packet(), { ...sender, port: 1234 });
  socket.emit('message', packet({ apiVersion: '1.0.0' }), sender);
  socket.emit('message', packet({ serviceId: 'other-store' }), sender);
  socket.emit('message', packet({ port: '9779' }), sender);
  socket.emit('message', packet(), sender); socket.emit('message', packet(), sender);
  assert.deepEqual(candidates, [{ ip: '192.168.20.40', port: 9779, serviceId: 'service-id', name: 'Kitchen computer' }]);
  controller.abort(); await done; assert.equal(socket.closes, 1);
});

test('abort before binding cannot send packets or leave a late-bound socket open', async () => {
  const { socket, controller, discover, clock } = fixture();
  const done = discover({ signal: controller.signal, onCandidate() {} });
  controller.abort(); socket.listening(); await done;
  assert.equal(socket.packets.length, 0); assert.equal(socket.listenerCount('message'), 0); assert.equal(clock.timers.size, 0);
  assert.equal(socket.closes, 2); // First closure while binding, and the late-listening cleanup.
});

test('failed socket/send paths reject once and release resources', async () => {
  for (const mode of ['socket', 'send']) {
    const { socket, controller, discover, clock } = fixture();
    const done = discover({ signal: controller.signal, onCandidate() {} });
    done.catch(() => {});
    if (mode === 'socket') socket.emit('error', new Error('bind failed'));
    else { socket.failSend = true; socket.listening(); }
    await assert.rejects(done, /failed/);
    controller.abort(); socket.emit('error', new Error('late error'));
    assert.equal(socket.closes, 1); assert.equal(clock.timers.size, 0);
  }
});

test('native adapter bounds candidate delivery and ignores packets after closing', async () => {
  const { socket, controller, discover, candidates } = fixture();
  const done = discover({ signal: controller.signal, onCandidate: value => candidates.push(value) }); socket.listening();
  for (let index = 1; index <= 100; index++) socket.emit('message', packet(), { address: `192.168.20.${index}`, port: 18778 });
  assert.equal(candidates.length, 64);
  controller.abort(); await done;
  socket.emit('message', packet(), { address: '192.168.20.200', port: 18778 });
  assert.equal(candidates.length, 64);
});
