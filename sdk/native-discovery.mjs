import { createSocket } from 'node:dgram';
import { networkInterfaces } from 'node:os';
import { isIP } from 'node:net';

const MESSAGE = Buffer.from('ENTREE_PRINT_DISCOVER', 'utf8');

// UDP belongs in Electron's trusted main/native layer, never an ordinary browser renderer.
// Construction does no I/O; the returned function is the SDK's discovery transport.
export function createNativeDiscovery({ port = 9778 } = {}, dependencies = {}) {
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new RangeError('Discovery port must be between 1 and 65535.');
  const io = { createSocket, networkInterfaces, setTimeout, clearTimeout, ...dependencies };
  return ({ signal, timeoutMs = 3000, serviceId, onCandidate }) => new Promise((resolve, reject) => {
    if (signal?.aborted) { resolve(); return; }
    if (!Number.isFinite(timeoutMs) || timeoutMs < 1 || timeoutMs > 30000 || typeof onCandidate !== 'function') {
      reject(new TypeError('A bounded timeout and onCandidate callback are required.')); return;
    }
    let socket;
    try { socket = io.createSocket('udp4'); } catch (error) { reject(error); return; }
    let closed = false;
    let timer;
    const seen = new Set();
    const closeSocket = () => { try { socket.close(); } catch { /* It may still be binding; listening handler closes it then. */ } };
    const finish = error => {
      if (closed) return;
      closed = true; io.clearTimeout(timer); signal?.removeEventListener('abort', cancel);
      socket.removeListener('message', receive); closeSocket();
      if (error) reject(error); else resolve();
    };
    const cancel = () => finish();
    const receive = (bytes, sender) => {
      if (closed || seen.size >= 64 || bytes.length > 8192 || sender.port !== port || isIP(sender.address) !== 4) return;
      let value;
      try { value = JSON.parse(bytes.toString('utf8')); } catch { return; }
      if (value?.ok !== true || value.service !== 'entree-print-plugin' || value.protocol !== 'entree-print' ||
        value.apiVersion !== '0.0.1' || typeof value.serviceId !== 'string' || value.serviceId.length < 1 || value.serviceId.length > 128 ||
        !Number.isInteger(value.port) || value.port < 1 || value.port > 65535 ||
        (serviceId && value.serviceId !== serviceId)) return;
      const key = `${sender.address}:${value.port}`;
      if (seen.has(key)) return;
      seen.add(key);
      try {
        // Ignore advertised address arrays: only probe the actual datagram sender.
        // The SDK separately verifies a fresh HTTP nonce and service identity before authorization.
        onCandidate({ ip: sender.address, port: value.port, serviceId: value.serviceId,
          name: typeof value.name === 'string' ? value.name.slice(0, 128) : 'ENTREE Print' });
      } catch (error) { finish(error); }
    };
    socket.on('error', error => { if (!closed) finish(error); });
    socket.on('message', receive);
    socket.once('listening', () => {
      if (closed) { closeSocket(); return; }
      try {
        socket.setBroadcast(true);
        const destinations = new Set(['127.0.0.1', '255.255.255.255']);
        for (const entries of Object.values(io.networkInterfaces())) for (const entry of entries ?? []) {
          if (entry.internal || (entry.family !== 'IPv4' && entry.family !== 4) || isIP(entry.address) !== 4 || isIP(entry.netmask) !== 4) continue;
          const address = entry.address.split('.').map(Number);
          const mask = entry.netmask.split('.').map(Number);
          if (mask.every(byte => byte === 255)) continue;
          destinations.add(address.map((byte, index) => (byte & mask[index]) | (255 ^ mask[index])).join('.'));
        }
        let remaining = destinations.size;
        let successes = 0;
        for (const destination of destinations) socket.send(MESSAGE, port, destination, error => {
          if (closed) return;
          if (!error) successes++;
          if (--remaining === 0 && !successes) finish(error ?? new Error('No discovery datagram could be sent.'));
        });
      } catch (error) { finish(error); }
    });
    signal?.addEventListener('abort', cancel, { once: true });
    timer = io.setTimeout(() => finish(), timeoutMs);
    try { socket.bind(0, '0.0.0.0'); } catch (error) { finish(error); }
  });
}
