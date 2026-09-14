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
