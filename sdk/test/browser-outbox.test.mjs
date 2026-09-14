import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, writeFile, access, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

test('real Chromium IndexedDB commits, survives browser restart, rejects stale updates and locks across tabs', { timeout: 45000 }, async () => {
  assert.equal(typeof WebSocket, 'function', 'This browser integration test needs Node.js 22+.');
  const candidates = [join(process.env['ProgramFiles(x86)'] || '', 'Microsoft/Edge/Application/msedge.exe'),
    join(process.env.ProgramFiles || '', 'Microsoft/Edge/Application/msedge.exe'),
    join(process.env.ProgramFiles || '', 'Google/Chrome/Application/chrome.exe')];
  let browser;
  for (const candidate of candidates) { try { await access(candidate); browser = candidate; break; } catch {} }
  assert.ok(browser, 'Install Edge or Chrome to run the browser storage check.');
  const directory = await mkdtemp(join(tmpdir(), 'EntreeOutboxBrowser-'));
  const profile = join(directory, 'profile'), page = join(directory, 'test.html');
  await writeFile(page, '<!doctype html><title>Entree synthetic storage test</title>');
  const module = (await readFile(new URL('../outbox.mjs', import.meta.url), 'utf8')).replace(/^export /gm, '');
  let running;
  async function start() {
    const process = spawn(browser, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
      '--disable-background-networking', '--remote-debugging-port=0', '--remote-debugging-address=127.0.0.1',
      `--user-data-dir=${profile}`, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
    const exited = new Promise(resolve => process.once('exit', resolve));
    let socket;
    running = { async stop() {
      socket?.close(); if (process.exitCode === null) process.kill(); await exited;
    } };
    let address;
    const end = Date.now() + 15000;
    while (!address) {
      assert.equal(process.exitCode, null, 'Browser exited before debugging was ready.');
      assert.ok(Date.now() < end, 'Browser startup timed out.');
      try {
        const [port, path] = (await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).trim().split(/\r?\n/);
        if (port && path) address = `ws://127.0.0.1:${port}${path}`;
      } catch {}
      if (!address) await new Promise(resolve => setTimeout(resolve, 30));
    }
    socket = new WebSocket(address);
    await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
    let sequence = 0; const requests = new Map();
    socket.addEventListener('message', event => {
      const message = JSON.parse(event.data), pending = requests.get(message.id);
      if (pending) { requests.delete(message.id); message.error ? pending.reject(new Error(JSON.stringify(message.error))) : pending.resolve(message.result); }
    });
    function send(method, params = {}, sessionId) {
      const id = ++sequence;
      return new Promise((resolve, reject) => { requests.set(id, { resolve, reject }); socket.send(JSON.stringify({ id, method, params, sessionId })); });
    }
    async function tab() {
      const target = await send('Target.createTarget', { url: pathToFileURL(page).href });
      const { sessionId } = await send('Target.attachToTarget', { targetId: target.targetId, flatten: true });
      async function evaluate(expression) {
        const reply = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, sessionId);
        assert.equal(reply.exceptionDetails, undefined, JSON.stringify(reply.exceptionDetails));
        return reply.result.value;
      }
      // Wait for the file origin; about:blank cannot own this persistent store.
      while (!(await evaluate('location.protocol === "file:"'))) await new Promise(resolve => setTimeout(resolve, 20));
      await evaluate(`(()=>{${module}; globalThis.store = createIndexedDbOutboxStorage({name:'synthetic-outbox'}); return true;})()`);
      return evaluate;
    }
    return { tab, send };
  }
  try {
    let host = await start(); const a = await host.tab(), b = await host.tab();
    const seed = { serviceId: 'server', idempotencyKey: '饭 / +%#', fingerprint: 'fixture', printer: 'cashier', state: 'pending' };
    const [first, second] = await Promise.all([a(`store.add(${JSON.stringify(seed)})`), b(`store.add(${JSON.stringify(seed)})`)]);
    assert.equal(first.localId, second.localId); assert.equal((await a('store.list()')).length, 1);
    const updated = await a(`store.update(${JSON.stringify(first)}, {...${JSON.stringify(first)}, state:'prepared'})`);
    assert.equal(updated.version, 2);
    assert.equal(await b(`store.update(${JSON.stringify(first)}, ${JSON.stringify(first)}).then(()=>null,e=>e.code)`), 'OUTBOX_CONFLICT');
    await a(`globalThis.held = false; globalThis.done = store.withQueue('server','cashier',()=>new Promise(resolve=>{globalThis.release=resolve;globalThis.held=true})); true`);
    while (!(await a('held'))) await new Promise(resolve => setTimeout(resolve, 10));
    await b(`globalThis.entered=false; globalThis.waiter=store.withQueue('server','cashier',()=>{globalThis.entered=true}); true`);
    assert.equal(await b('entered'), false);
    assert.equal(await b(`store.withQueue('server','kitchen',()=> 'independent')`), 'independent');
    await a('release(); done.then(()=>true)'); await b('waiter.then(()=>true)'); assert.equal(await b('entered'), true);
    await a('store.close()'); await b('store.close()'); await host.send('Browser.close'); await running.stop();
    // The profile is retained; remove only the old endpoint locator before starting the next browser.
    await rm(join(profile, 'DevToolsActivePort'), { force: true });
    host = await start(); const reopened = await host.tab();
    const records = await reopened('store.list()'); assert.equal(records.length, 1); assert.equal(records[0].state, 'prepared');
    await reopened('store.close()'); await host.send('Browser.close');
  } finally {
    await running?.stop();
    // This verified, newly created directory contains only the synthetic browser profile.
    assert.ok(directory.startsWith(join(tmpdir(), 'EntreeOutboxBrowser-')));
    await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
});
