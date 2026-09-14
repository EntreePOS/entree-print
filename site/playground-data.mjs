export const presets = {
  receipt: [{ type: 'html', html: '<div style="font-family: Arial; font-size: 14px"><h2 style="text-align:center">ENTREE CAFE</h2><p style="text-align:center">Order 1042 · Takeaway</p><hr style="border:0; border-top:1px solid black"><table style="width:100%"><tr><td>炒饭 Fried rice × 2</td><td style="text-align:right">24.00</td></tr><tr><td>Jasmine tea × 1</td><td style="text-align:right">3.50</td></tr></table><hr style="border:0; border-top:1px solid black"><p style="text-align:right; font-weight:bold">Total $27.50</p><p style="text-align:center">谢谢光临 · Thank you!</p></div>' }],
  kitchen: [{ type: 'html', html: '<div style="font-family: Arial; font-size:18px"><h1>TABLE 12</h1><p>Order 1042 · 18:42</p><hr style="border:0; border-top:1px solid black"><h2>2 × 炒饭</h2><p>Fried rice</p><p style="font-weight:bold">No onion · 不要洋葱</p><hr style="border:0; border-top:1px solid black"><h2>1 × Dumplings</h2><p>酱汁分开 · Sauce on the side</p></div>' }],
  codes: [{ type: 'html', html: '<h2 style="text-align:center">Order 1042 · 谢谢</h2><p style="text-align:center">Scan to collect your order</p>' }, { type: 'qrcode', value: 'ORDER1042', sizeMm: 24 }, { type: 'barcode', format: 'code128', value: 'ORDER1042', showText: true }]
};

export function parseContent(source) {
  if (source.length > 250000) throw new Error('Keep this playground example under 250,000 characters.');
  const value = JSON.parse(source);
  if (!Array.isArray(value) || !value.length || value.length > 200) throw new Error('Use an array of 1–200 content blocks.');
  for (const block of value) {
    if (!block || !['html', 'qrcode', 'barcode'].includes(block.type)) throw new Error('Supported blocks: html, qrcode and barcode.');
    if (block.type === 'html' && typeof block.html !== 'string') throw new Error('An HTML block needs an html string.');
    if (block.type !== 'html' && (typeof block.value !== 'string' || !block.value)) throw new Error('A code block needs a nonempty value string.');
  }
  return value;
}

export function apiCode({ content, host, port, protocol, printer, width }) {
  return `import EntreePrint from './sdk/entree-print.mjs';

const print = await EntreePrint.connect({
  ip: ${JSON.stringify(host)},
  port: ${port},
  protocol: ${JSON.stringify(protocol)},
  token: settings.printServiceToken
});

const printers = await print.getPrinters();
const content = ${JSON.stringify(content, null, 2)};

const ticket = print.target(${JSON.stringify(printer)})${width === 'auto' ? '' : `.setWidth(${Number(width)})`}
  .setContent(content);
const rendered = await ticket.render();
previewFrame.setAttribute('sandbox', '');
previewFrame.srcdoc = rendered.html;

// Call on the operator's print action. Persist savedIntent.id
// before sending; reuse it and this ticket when retrying.
async function printReceipt() {
  return ticket.print({ idempotencyKey: savedIntent.id });
}`;
}

export const previewPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src 'none'; font-src 'none'; form-action 'none'; base-uri 'none'";
export function frameDocument(body, widthMm, scale = 1) {
  return `<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="${previewPolicy}"><style>html,body{margin:0;background:white;color:black;font:14px Arial,sans-serif}*{box-sizing:border-box}#receipt-sheet{width:${widthMm}mm;transform:scale(${scale});transform-origin:top left;overflow-wrap:anywhere}.code-placeholder{border:1px solid #8793a1;margin:12px 4px;padding:16px 8px;text-align:center;font:12px Arial;color:#465568}</style></head><body><div id="receipt-sheet">${body}</div></body></html>`;
}
