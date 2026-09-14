import { webcrypto } from 'node:crypto';
import { createEntreePrint } from './entree-print.mjs';
import { createNativeDiscovery } from './native-discovery.mjs';

// Import this entry point in Node or Electron main. It does not open sockets until search/recovery.
export function createNativeEntreePrint(options = {}, { discoveryPort = 9778, powerMonitor } = {}) {
  return createEntreePrint(options, {
    crypto: webcrypto,
    discover: createNativeDiscovery({ port: discoveryPort }),
    ...(powerMonitor ? { subscribeResume(handler) {
      powerMonitor.on('resume', handler);
      return () => powerMonitor.removeListener('resume', handler);
    } } : {})
  });
}

export const EntreePrint = createNativeEntreePrint();
export default EntreePrint;
