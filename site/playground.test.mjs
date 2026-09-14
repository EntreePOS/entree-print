import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { presets, parseContent, apiCode, frameDocument, previewPolicy } from './playground-data.mjs';

test('all presets parse and produce executable API examples without submitting prints', async () => {
  const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
  for (const preset of Object.values(presets)) {
    const content = parseContent(JSON.stringify(preset));
    const calls = [];
    const ticket = { setContent(value) { assert.deepEqual(value, content); return this; }, setWidth(mm) { assert.equal(mm, 72); return this; }, async render() { return { html: '<svg></svg>' }; }, print() { throw new Error('Example printed on execution'); } };
    const EntreePrint = { async connect(options) { assert.equal(options.ip, 'host";throw Error();//'); assert.equal(options.token, 'test-secret'); calls.push('connect'); return { async getPrinters() { calls.push('printers'); return [{name:'cashier'}]; }, target(name) { assert.equal(name, 'cashier'); return ticket; } }; } };
    const previewFrame = { setAttribute(name, value) { assert.equal(name, 'sandbox'); assert.equal(value, ''); } };
    const code = apiCode({ content, host: 'host";throw Error();//', port: 9779, protocol: 'http', printer:'cashier', width:'72', token: 'must-not-appear' });
    assert.ok(!code.includes('must-not-appear'));
    await new AsyncFunction('EntreePrint', 'settings', 'previewFrame', code.replace(/^import .*;\n/, ''))(EntreePrint, {printServiceToken:'test-secret'}, previewFrame);
    assert.deepEqual(calls, ['connect', 'printers']); assert.equal(previewFrame.srcdoc, '<svg></svg>');
  }
});
test('invalid JSON and unsupported content cannot become previews', () => {
  for (const source of ['{', '{}', '[]', '[null]', '[{"type":"image"}]', '[{"type":"html"}]', '[{"type":"qrcode","value":""}]', ' '.repeat(250001)]) assert.throws(() => parseContent(source));
});
test('driver width is omitted by default and iframe policy forbids network and scripts', () => {
  assert.doesNotMatch(apiCode({ content:presets.receipt, host:'127.0.0.1', port:9779, protocol:'http', printer:'cashier', width:'auto' }), /setWidth/);
  const html = frameDocument('<p>Test</p>', 72, .9);
  assert.ok(html.indexOf(previewPolicy) < html.indexOf('<p>Test</p>'));
  assert.match(previewPolicy, /default-src 'none'/); assert.doesNotMatch(previewPolicy, /script-src|connect-src/);
});
test('playground has no print submission or token persistence path', async () => {
  const source = await readFile(new URL('./playground.mjs', import.meta.url), 'utf8');
  assert.doesNotMatch(source, /\.print\(|\.sendCommand\(|\.openDrawer\(|\.beep\(|localStorage|sessionStorage/);
  const html = await readFile(new URL('./playground.html', import.meta.url), 'utf8');
  assert.match(html, /id="preview"[^>]*sandbox=""/);
});

// Exercise the actual page controller with a small DOM fixture. No network or
// printing is performed; the real-browser check covers HTML presentation.
async function loadPlayground({ token = '', probe, connect, immediateDeadline = false } = {}) {
  const elements = new Map();
  const initial = { host:'127.0.0.1', port:'9779', protocol:'http', width:'auto', preset:'receipt', printer:'cashier', token };
  const element = id => {
    if (!elements.has(id)) elements.set(id, { value:initial[id] ?? '', textContent:'', dataset:{}, style:{}, disabled:false,
      parentElement:{clientWidth:340}, listeners:{}, setAttribute() {},
      addEventListener(name, handler) { this.listeners[name] = handler; },
      replaceChildren(...children) { this.value = children[0]?.value ?? ''; }
    });
    return elements.get(id);
  };
  let connectionEvent;
  const sdk = { disconnect() {}, connect: connect ?? (() => { throw new Error('Unexpected authenticated connection'); }),
    subscribe(handler) { connectionEvent = handler; } };
  const document = { getElementById:element, querySelector:element,
    createElement() { return { content:{querySelectorAll:() => []}, innerHTML:'' }; } };
  const dependencies = { EntreePrint:sdk, document, presets, parseContent, apiCode, frameDocument,
    ResizeObserver:class { observe() {} }, Option:class { constructor(label,value) { this.value = value; } },
    location:{origin:'http://127.0.0.1:19780'}, addEventListener() {}, fetch:probe,
    setTimeout:immediateDeadline ? (callback, ms) => { assert.equal(ms,3000); queueMicrotask(callback); return 0; } : setTimeout,
    clearTimeout:immediateDeadline ? () => {} : clearTimeout };
  const source = (await readFile(new URL('./playground.mjs', import.meta.url), 'utf8'))
    .replace(/^import .*;\r?\n/gm, '').replace('void connectToService(true);', 'await connectToService(true);');
  const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
  await new AsyncFunction(...Object.keys(dependencies), source)(...Object.values(dependencies));
  return { element, connectionEvent };
}

test('startup probes localhost and falls back to an editable preview without a plugin', async () => {
  let probes = 0;
  const { element } = await loadPlayground({ probe: async (url, options) => {
    probes++; assert.equal(url,'http://127.0.0.1:9779/api/health'); assert.equal(options.credentials,'omit');
    throw new TypeError('Connection refused');
  } });
  assert.equal(probes,1);
  assert.equal(element('preview-kind').textContent,'Preview mode');
  assert.equal(element('connection-badge').textContent,'Preview mode');
  assert.match(element('preview').srcdoc,/Fried rice/);
  assert.equal(element('render').disabled,true);
  assert.equal(element('connection-fields').disabled,false);
  assert.equal(element('connection-status').dataset.error,'false');
  element('content').value = JSON.stringify([{type:'html',html:'<p>Edited offline</p>'}]);
  element('content').listeners.input();
  assert.match(element('preview').srcdoc,/Edited offline/);
});

test('an unresponsive local plugin times out into preview mode', async () => {
  const { element } = await loadPlayground({ immediateDeadline:true, probe: (url,{signal}) =>
    new Promise((resolve,reject) => signal.addEventListener('abort', () => reject(new Error('Timeout')))) });
  assert.equal(element('connection-badge').textContent,'Preview mode');
  assert.equal(element('connection-fields').disabled,false);
});

test('a local plugin requiring authorization keeps preview available and shows token setup', async () => {
  const { element } = await loadPlayground({ probe: async () => ({ok:true,json:async () => ({service:'entree-print-plugin',apiVersion:'0.0.1'})}) });
  assert.equal(element('.connection-panel').open,true);
  assert.match(element('connection-status').textContent,/Local plugin found.*access token/);
  assert.equal(element('preview-kind').textContent,'Preview mode');
});

test('authorized startup obtains printers from the library and heartbeat loss restores preview mode', async () => {
  const calls = [];
  const { element, connectionEvent } = await loadPlayground({ token:'a'.repeat(32), connect:async config => {
    calls.push('connect'); assert.equal(config.ip,'127.0.0.1'); assert.equal(config.port,9779); assert.equal(config.requestTimeoutMs,3000);
    return {getPrinters:async () => { calls.push('printers'); return [{name:'cashier'}]; }};
  } });
  assert.deepEqual(calls,['connect','printers']);
  assert.equal(element('connection-badge').textContent,'Connected');
  assert.equal(element('render').disabled,false);
  connectionEvent({type:'connection',data:{state:'offline'}});
  assert.equal(element('preview-kind').textContent,'Preview mode');
  assert.equal(element('render').disabled,true);
  connectionEvent({type:'connection',data:{state:'online'}});
  assert.equal(element('preview-kind').textContent,'Browser draft');
  assert.equal(element('render').disabled,false);
});
