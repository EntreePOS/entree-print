const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

async function demo() {
  const elements = new Map();
  const calls = [];
  const element = id => {
    if (!elements.has(id)) elements.set(id, {
      value: id === 'paper' ? '72' : '', style: {}, textContent: '', disabled: false,
      handlers: {}, setAttribute() {}, replaceChildren() {},
      addEventListener(type, handler) { this.handlers[type] = handler; },
      showModal() { this.open = true; }, close() { this.open = false; },
      click() { return this.handlers.click?.(); }
    });
    return elements.get(id);
  };
  const lodop = { CVERSION: 'test', On_Return: null };
  for (const method of ['PRINT_INIT', 'SET_PRINTER_INDEX', 'SET_PRINT_COPIES', 'SET_PRINT_PAGESIZE',
    'SET_PRINT_MODE', 'ADD_PRINT_HTM', 'ADD_PRINT_HTML']) {
    lodop[method] = (...args) => { calls.push({ method, args }); return true; };
  }
  lodop.PREVIEW = (...args) => { calls.push({ method: 'PREVIEW', args }); };
  lodop.PRINT = () => { calls.push({ method: 'PRINT' }); queueMicrotask(() => lodop.On_Return?.(1, true)); };
  const context = vm.createContext({
    document: { getElementById: element }, window: { getCLodop: () => lodop },
    Option: function(text, value) { this.text = text; this.value = value; },
    crypto: { randomUUID: () => '12345678-1234-1234-1234-123456789012' },
    setTimeout, clearTimeout, Date, console,
    fetch: async (path, options) => {
      calls.push({ path, body: options?.body ? JSON.parse(options.body) : null });
      return { ok: true, json: async () => path === '/api/session'
        ? { token: 'session', printers: ['cashier'], defaultPrinter: 'cashier' }
        : path === '/api/prepare'
        ? { id: 'prepared', imageUrl: '/image', textPreviewUrl: '/vector', textRuns: 40, paperWidthMm: 72, heightMm: 220, width: 546, height: 1670 }
        : { status: 'submitted', message: 'submitted' } };
    }
  });
  vm.runInContext(fs.readFileSync(__dirname + '/wwwroot/app.js', 'utf8'), context);
  await new Promise(resolve => setImmediate(resolve));
  return { context, element, calls, lodop };
}

test('Opening and preparing the demo never submits a print', async () => {
  const { calls, element } = await demo();
  assert.equal(calls.filter(call => call.path === '/api/prepare').length, 1);
  assert.equal(calls.some(call => call.path === '/api/print' || call.method === 'PRINT'), false);
  assert.equal(element('print-a').disabled, false);
});

test('Both C-Lodop previews receive the frozen HTML and never call PRINT', async () => {
  const { element, calls } = await demo();
  const html = calls.find(call => call.path === '/api/prepare').body.html;
  for (const mode of ['b', 'c']) {
    const pending = element('preview-' + mode).click();
    assert.equal(element('clodop-dialog').open, true);
    await element('close-preview').click();
    await pending;
  }
  assert.equal(calls.find(call => call.method === 'ADD_PRINT_HTM').args[4], html);
  assert.equal(calls.find(call => call.method === 'ADD_PRINT_HTML').args[4], html);
  assert.equal(calls.filter(call => call.method === 'PREVIEW').length, 2);
  assert.equal(calls.some(call => call.method === 'PRINT'), false);
});

test('Editing HTML invalidates all print buttons until prepared again', async () => {
  const { element } = await demo();
  element('html').value = '<p>Changed</p>';
  element('html').handlers.input();
  for (const mode of ['a', 'b', 'c', 'd']) assert.equal(element('print-' + mode).disabled, true);
});

test('D preview never submits and a double click prints the prepared text once', async () => {
  const { element, calls } = await demo();
  await element('preview-d').click();
  assert.equal(element('vector').hidden, false);
  assert.equal(calls.some(call => call.path === '/api/print'), false);
  await Promise.all([element('print-d').click(), element('print-d').click()]);
  const submissions = calls.filter(call => call.path === '/api/print');
  assert.equal(submissions.length, 1);
  assert.equal(submissions[0].body.method, 'd');
  assert.equal(submissions[0].body.id, 'prepared');
});

test('A rapid double click sends one PNG submission with a stable request ID', async () => {
  const { element, calls } = await demo();
  await Promise.all([element('print-a').click(), element('print-a').click()]);
  const submissions = calls.filter(call => call.path === '/api/print');
  assert.equal(submissions.length, 1);
  assert.equal(submissions[0].body.id, 'prepared');
  assert.equal(submissions[0].body.requestId.length, 32);
});

test('A C-Lodop print requests exactly one copy and distinguishes failure', async () => {
  const { element, calls, lodop } = await demo();
  lodop.PRINT = () => queueMicrotask(() => lodop.On_Return?.(1, false));
  await element('print-b').click();
  assert.equal(calls.find(call => call.method === 'SET_PRINT_COPIES').args[0], 1);
  assert.match(element('notice').textContent, /unsuccessful submission/);
  assert.equal(element('print-b').disabled, false);
});
