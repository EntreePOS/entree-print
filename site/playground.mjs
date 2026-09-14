import EntreePrint from './sdk/entree-print.mjs';
import { presets, parseContent, apiCode, frameDocument } from './playground-data.mjs';

const $ = id => document.getElementById(id);
let library = null, content = null, rendered = null, busy = false, revision = 0;
let displayedBody = '', displayedWidth = 72;
const escape = value => String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');
function feedback(id, message, error = false) { $(id).textContent = message; $(id).dataset.error = String(error); }
function syncButtons() { $('render').disabled = !library || !content || busy; $('download').disabled = !rendered || busy; }
function options() { return { host: $('host').value.trim(), port: Number($('port').value), protocol: $('protocol').value, printer: $('printer').value, width: $('width').value }; }
function updateCode() {
  $('api-code').textContent = content ? apiCode({ ...options(), content }) : '// Fix the receipt JSON to generate the API example.';
}

// The draft uses a deliberately small inert HTML subset. The service remains
// authoritative for supported HTML/CSS, printer dimensions and code encoding.
function draftHtml(html) {
  const template = document.createElement('template');
  template.innerHTML = html;
  const allowed = new Set('DIV SPAN P H1 H2 H3 H4 H5 H6 HR BR B STRONG I EM U SMALL TABLE THEAD TBODY TFOOT TR TD TH UL OL LI STYLE'.split(' '));
  for (const element of template.content.querySelectorAll('*')) {
    if (!allowed.has(element.tagName)) { element.remove(); continue; }
    for (const attribute of [...element.attributes]) if (!['style', 'colspan', 'rowspan'].includes(attribute.name)) element.removeAttribute(attribute.name);
  }
  return template.innerHTML;
}
function showFrame() {
  const available = Math.min(displayedWidth * 96 / 25.4, $('preview').parentElement.clientWidth - 18);
  $('preview').style.width = `${Math.max(100, available)}px`;
  $('preview').srcdoc = frameDocument(displayedBody, displayedWidth, Math.min(1, Math.max(100, available) / (displayedWidth * 96 / 25.4)));
}
function draft() {
  rendered = null; revision++;
  $('preview-kind').textContent = 'Browser draft';
  displayedWidth = $('width').value === 'auto' ? 72 : Number($('width').value);
  $('preview-note').textContent = `Approximate HTML layout at ${displayedWidth} mm. Connect for the driver's exact layout and scannable codes.`;
  try {
    content = parseContent($('content').value);
    displayedBody = content.map(block => block.type === 'html' ? draftHtml(block.html) : `<div class="code-placeholder">${block.type === 'qrcode' ? 'QR code' : escape(block.format || 'Barcode')} · ${escape(block.value)}<br>Render with service to generate the code</div>`).join('');
    feedback('validation', `${content.length} content block${content.length === 1 ? '' : 's'}. The service validates print compatibility.`);
    $('content').setAttribute('aria-invalid', 'false');
    feedback('render-status', 'Draft updated. Render with the service to check the final layout.');
  } catch (error) {
    content = null; displayedBody = '<p style="padding:16px">Fix the receipt JSON to update the preview.</p>';
    feedback('validation', error.message, true); $('content').setAttribute('aria-invalid', 'true');
    feedback('render-status', 'No current rendered preview.');
  }
  showFrame(); updateCode(); syncButtons();
}
function resetExample() { $('content').value = JSON.stringify(presets[$('preset').value], null, 2); draft(); }
function clearConnection() {
  library = null; EntreePrint.disconnect();
  $('printer').replaceChildren(new Option('cashier (example)', 'cashier'));
  $('printer').disabled = true; $('connection-fields').disabled = false; $('disconnect').hidden = true;
  $('connection-badge').textContent = 'Optional';
  draft();
}
$('connection-form').addEventListener('submit', async event => {
  event.preventDefault(); if (busy) return;
  clearConnection(); busy = true; syncButtons(); $('connection-fields').disabled = true;
  feedback('connection-status', 'Connecting and reading available printers…');
  try {
    const config = options();
    const connected = await EntreePrint.connect({ ip: config.host, port: config.port, protocol: config.protocol, token: $('token').value });
    const printers = await connected.getPrinters();
    if (!Array.isArray(printers) || !printers.length || printers.some(printer => typeof printer?.name !== 'string' || !printer.name)) throw new Error('The service did not return an available printer list.');
    library = connected;
    $('printer').replaceChildren(...printers.map(printer => new Option(printer.name, printer.name)));
    $('printer').disabled = false; $('disconnect').hidden = false;
    $('connection-badge').textContent = 'Connected';
    feedback('connection-status', `Connected. ${printers.length} printer${printers.length === 1 ? '' : 's'} available from the print library.`);
    draft();
  } catch (error) {
    clearConnection();
    feedback('connection-status', `${error.code ? error.code + ': ' : ''}${error.message} Check the address, token, permitted website and browser network access.`, true);
  } finally { busy = false; syncButtons(); }
});
$('disconnect').addEventListener('click', () => {
  clearConnection(); $('token').value = ''; feedback('connection-status', 'Disconnected. The token field was cleared.');
});
$('render').addEventListener('click', async () => {
  if (!library || !content || busy) return;
  draft();
  const started = revision;
  busy = true; rendered = null; syncButtons(); feedback('render-status', 'Preparing the Windows layout…');
  try {
    let ticket = library.target($('printer').value).setContent(content);
    if ($('width').value !== 'auto') ticket = ticket.setWidth(Number($('width').value));
    const result = await ticket.render();
    if (revision !== started) return;
    if (!Number.isFinite(result.widthMm) || result.widthMm <= 0 || result.widthMm > 100) throw new Error('The service returned an invalid preview width.');
    rendered = result;
    displayedBody = result.html; displayedWidth = result.widthMm; showFrame();
    $('preview-kind').textContent = 'Service render';
    $('preview-note').textContent = `Windows layout · ${result.widthMm.toFixed(2)} mm printable width. Scaled to fit this screen; long receipts scroll.`;
    feedback('render-status', 'Rendered successfully. No print job was submitted.'); syncButtons();
  } catch (error) {
    if (revision === started) feedback('render-status', `${error.code ? error.code + ': ' : ''}${error.message}`, true);
  } finally { busy = false; syncButtons(); }
});
$('download').addEventListener('click', () => {
  if (!rendered) return;
  const url = URL.createObjectURL(new Blob([rendered.html], { type: 'text/html;charset=utf-8' }));
  const link = document.createElement('a'); link.href = url; link.download = 'entree-receipt.html'; link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
});
$('content').addEventListener('input', draft);
for (const id of ['width', 'printer']) $(id).addEventListener('change', draft);
for (const id of ['host', 'port', 'protocol']) $(id).addEventListener('input', updateCode);
$('preset').addEventListener('change', resetExample); $('reset').addEventListener('click', resetExample);
$('page-origin').textContent = location.origin;
EntreePrint.subscribe(event => {
  if (!library || event.type !== 'connection') return;
  $('connection-badge').textContent = event.data.state === 'online' ? 'Connected' : event.data.state;
  if (event.data.state !== 'online') feedback('connection-status', `Service ${event.data.state}. Check the connection before rendering.`, true);
  else feedback('connection-status', 'Service connection is online. Choose a printer to render.');
});
new ResizeObserver(showFrame).observe($('preview').parentElement);
addEventListener('pagehide', () => EntreePrint.disconnect());
resetExample();
