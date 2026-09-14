import EntreePrint from './sdk/entree-print.mjs';
import { presets, parseContent, apiCode, frameDocument } from './playground-data.mjs';

const $ = id => document.getElementById(id);
let library = null, content = null, rendered = null, busy = false, online = false, revision = 0;
let displayedBody = '', displayedWidth = 72;
const escape = value => String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');
function feedback(id, message, error = false) { $(id).textContent = message; $(id).dataset.error = String(error); }
function syncButtons() { $('render').disabled = !library || !online || !content || busy; $('download').disabled = !rendered || busy; }
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
  $('preview-kind').textContent = online ? 'Browser draft' : 'Preview mode';
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
  library = null; online = false; EntreePrint.disconnect();
  $('printer').replaceChildren(new Option('cashier (example)', 'cashier'));
  $('printer').disabled = true; $('connection-fields').disabled = false; $('disconnect').hidden = true;
  $('connection-badge').textContent = 'Preview mode';
  draft();
}
async function connectToService(automatic = false) {
  if (busy) return;
  clearConnection(); busy = true; syncButtons(); $('connection-fields').disabled = true;
  $('connection-badge').textContent = 'Connecting…';
  feedback('connection-status', automatic ? 'Checking the local plugin at 127.0.0.1:9779… You can edit the preview while it connects.' : 'Connecting and reading available printers…');
  try {
    const config = automatic ? { host: '127.0.0.1', port: 9779, protocol: 'http' } : options();
    if (automatic && $('token').value.length < 32) {
      // Health is public; printer access still uses the SDK's authenticated handshake.
      const controller = new AbortController();
      const deadline = setTimeout(() => controller.abort(), 3000);
      try {
        const response = await fetch('http://127.0.0.1:9779/api/health', { signal: controller.signal, credentials: 'omit', cache: 'no-store', redirect: 'error' });
        const health = response.ok ? await response.json() : null;
        if (health?.service !== 'entree-print-plugin' || health.apiVersion !== '0.0.1') throw new Error('No compatible local plugin was found.');
      } finally { clearTimeout(deadline); }
      const error = new Error('Local plugin found. Enter its access token to connect. Preview mode is ready to use.');
      error.code = 'AUTH_NOT_CONFIGURED';
      throw error;
    }
    const connected = await EntreePrint.connect({ ip: config.host, port: config.port, protocol: config.protocol, token: $('token').value, requestTimeoutMs: automatic ? 3000 : 10000 });
    const printers = await connected.getPrinters();
    if (!Array.isArray(printers) || !printers.length || printers.some(printer => typeof printer?.name !== 'string' || !printer.name)) throw new Error('The service did not return an available printer list.');
    library = connected; online = true;
    $('printer').replaceChildren(...printers.map(printer => new Option(printer.name, printer.name)));
    $('printer').disabled = false; $('disconnect').hidden = false;
    $('connection-badge').textContent = 'Connected';
    feedback('connection-status', `Connected. ${printers.length} printer${printers.length === 1 ? '' : 's'} available from the print library.`);
    draft();
  } catch (error) {
    clearConnection();
    if (automatic) {
      const needsToken = error.code === 'AUTH_NOT_CONFIGURED' || error.code === 'UNAUTHORIZED';
      feedback('connection-status', needsToken ? 'Local plugin found. Enter its access token to connect. Preview mode is ready to use.' : 'Local plugin unavailable. Preview mode is ready to use. Use Connect to try again.');
      if (needsToken) document.querySelector('.connection-panel').open = true;
    } else feedback('connection-status', `${error.code ? error.code + ': ' : ''}${error.message} Preview mode is still available. Check the address, token, permitted website and browser network access.`, true);
  } finally { busy = false; syncButtons(); }
}
$('connection-form').addEventListener('submit', async event => {
  event.preventDefault(); await connectToService();
});
$('disconnect').addEventListener('click', () => {
  clearConnection(); $('token').value = ''; feedback('connection-status', 'Disconnected. The token field was cleared.');
});
$('render').addEventListener('click', async () => {
  if (!library || !online || !content || busy) return;
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
  online = event.data.state === 'online';
  $('connection-badge').textContent = online ? 'Connected' : 'Preview mode';
  if (!online) {
    draft();
    feedback('connection-status', 'Plugin connection unavailable. Preview mode is ready to use; checking for reconnection.');
  }
  else {
    if (!rendered) $('preview-kind').textContent = 'Browser draft';
    feedback('connection-status', 'Service connection is online. Choose a printer to render.');
  }
  syncButtons();
});
new ResizeObserver(showFrame).observe($('preview').parentElement);
addEventListener('pagehide', () => EntreePrint.disconnect());
resetExample();
void connectToService(true);
