// Runs against the isolated demo. Only prepares receipts and reads SVG; never submits printing.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
(async () => {
  const base = 'http://127.0.0.1:19779';
  const session = await (await fetch(base + '/api/session')).json();
  const source = fs.readFileSync(__dirname + '/wwwroot/app.js', 'utf8');
  const context = vm.createContext({ $: () => ({ value: '72' }) });
  const html = vm.runInContext(source.slice(source.indexOf('function sample()'), source.indexOf('function sourcePreview(')) + '\nsample()', context);
  async function prepare(html, width = 72) {
    const response = await fetch(base + '/api/prepare', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Demo-Session': session.token }, body: JSON.stringify({ html, paperWidthMm: width }) });
    assert.equal(response.status, 200);
    return response.json();
  }
  const receipt = await prepare(html);
  assert.equal(receipt.textError, null, receipt.textError);
  assert.ok(receipt.textRuns > 30);
  const svg = await (await fetch(base + receipt.textPreviewUrl)).text();
  assert.match(svg, /<text/);
  assert.doesNotMatch(svg, /<image/);
  assert.match(svg, /END OF RECEIPT/);
  assert.match(svg, /欢迎光临/);
  assert.match(svg, /38.59/);
  console.log('PASS: full sample preserves text, Chinese, prices and footer; SVG has no bitmap.', receipt.id);
  const unsupported = await prepare('<p>Image receipt</p><img src="data:image/png;base64,AA==">');
  assert.equal(unsupported.textPreviewUrl, null);
  assert.match(unsupported.textError, /does not support.*img/);
  console.log('PASS: unsupported content disables D without disabling A/B/C. No print submitted.');
})().catch(error => { console.error(error); process.exitCode = 1; });
