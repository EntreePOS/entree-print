"use strict";
const $ = id => document.getElementById(id);
const state = { token: "", prepared: null, html: "", lodop: null, busy: false, loading: null, closePreview: null };
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
function notice(message, error = false) { $("notice").textContent = message; $("notice").className = error ? "error" : ""; }
function log(message) { $("log").textContent = `${new Date().toLocaleTimeString()}  ${message}\n${$("log").textContent}`; }
function controls() {
  const ready = !!state.prepared && !state.busy;
  $("prepare").disabled = state.busy || !state.token;
  $("printer").disabled = $("paper").disabled = $("html").disabled = $("reset").disabled = state.busy;
  $("preview-a").disabled = $("image-tab").disabled = !ready;
  $("print-a").disabled = !ready || !$("printer").value;
  $("preview-d").disabled = !ready || !state.prepared?.textPreviewUrl;
  $("print-d").disabled = $("preview-d").disabled || !$("printer").value;
  for (const mode of ["b", "c"]) {
    $("preview-" + mode).disabled = $("print-" + mode).disabled = !ready || !state.lodop || !$("printer").value;
  }
}
async function api(path, body) {
  const response = await fetch(path, body ? { method: "POST", headers: { "Content-Type": "application/json", "X-Demo-Session": state.token }, body: JSON.stringify(body) } : {});
  const result = await response.json();
  if (!response.ok) throw new Error(result.error || `HTTP ${response.status}`);
  return result;
}
function sample() {
  const width = Number($("paper").value);
  const ruler = width < 58 ? 30 : 50;
  return `<!doctype html><html><head><meta charset="utf-8"><style>
html,body{margin:0;padding:0;background:#fff;color:#000;width:${width}mm}
body{font-family:Arial,"Microsoft YaHei",sans-serif;font-size:10pt;line-height:1.35}
.ticket{padding:4mm;box-sizing:border-box;width:${width}mm}h1{font-size:19pt;line-height:1.15;margin:0 0 2mm}h2{font-size:13pt;margin:0 0 2mm}p{margin:1.5mm 0}.center{text-align:center}.small{font-size:8pt}.rule{border-top:1px solid #000;margin:3mm 0}table{border-collapse:collapse;width:100%;table-layout:fixed}td{vertical-align:top;padding:1.5mm 0;word-wrap:break-word}td.price{width:18mm;text-align:right;white-space:nowrap}.total{font-size:14pt;font-weight:bold}.ruler{width:${ruler}mm;border-bottom:1px solid black;border-left:1px solid black;border-right:1px solid black;height:3mm;margin:2mm 0}.fine{border-top:1px solid #000;margin-top:2mm}.medium{border-top:2px solid #000;margin-top:2mm}.thick{border-top:3px solid #000;margin-top:2mm}
</style></head><body><div class="ticket">
<div class="center"><h1>ENTREE</h1><h2>欢迎光临 · 歡迎光臨</h2><p class="small">PRINT QUALITY SAMPLE · #10086</p></div>
<div class="rule"></div><table><tr><td>Table / 桌号</td><td class="price">08</td></tr><tr><td>Guest / 客人</td><td class="price">2</td></tr></table>
<div class="rule"></div><table><tr><td><b>Item / 菜品</b></td><td class="price"><b>Amount</b></td></tr>
<tr><td>2 × 扬州炒饭<br><span class="small">Yangzhou fried rice</span></td><td class="price">$17.98</td></tr>
<tr><td>1 × 牛肉面<br><span class="small">Beef noodle soup</span></td><td class="price">$13.50</td></tr>
<tr><td>2 × 热茶 / 熱茶</td><td class="price">$4.00</td></tr></table>
<p><b>不要花生 / No peanuts</b></p><p class="small">长备注测试：少盐，不要香菜，酱汁另外放。Please put the sauce on the side and keep this entire instruction visible.</p>
<div class="rule"></div><table><tr><td>Subtotal</td><td class="price">$35.48</td></tr><tr><td>Tax</td><td class="price">$3.11</td></tr><tr class="total"><td>TOTAL</td><td class="price">$38.59</td></tr></table>
<div class="rule"></div><p><b>Character &amp; line checks</b></p><p style="font-size:8pt">8 pt · 中文细笔画 · café, crème brûlée</p><p style="font-size:10pt">10 pt · 中文繁體 · naïve, mañana</p><p style="font-size:12pt">12 pt · 欢迎光临 · $38.59</p><p>0123456789 · Il1 O0 B8</p>
<div class="fine"></div><div class="medium"></div><div class="thick"></div><p class="small">Rules: 1 / 2 / 3 CSS px</p>
<div class="ruler"></div><p class="small">This ruler should measure ${ruler} mm.</p><div class="rule"></div>
<p class="center"><b>谢谢 · 謝謝 · Thank you</b></p><p class="center small">END OF RECEIPT — check this line is present</p>
</div></body></html>`;
}
function sourcePreview(html) {
  $("source").srcdoc = html;
  const width = `${$("paper").value}mm`;
  $("source").style.width = $("raster").style.width = width;
  $("dimensions").textContent = width;
}
function showPreview(image) {
  $("vector").hidden = true;
  $("raster").hidden = !image; $("source").hidden = image;
  $("source-tab").setAttribute("aria-pressed", String(!image));
  $("image-tab").setAttribute("aria-pressed", String(image));
}
async function prepare() {
  if (state.busy) return;
  state.busy = true; state.prepared = null; controls(); showPreview(false);
  state.html = $("html").value;
  sourcePreview(state.html);
  notice("Preparing A’s PNG and D’s positioned text. B and C receive the same HTML.");
  try {
    state.prepared = await api("/api/prepare", { html: state.html, paperWidthMm: Number($("paper").value) });
    $("raster").src = state.prepared.imageUrl;
    $("vector").src = state.prepared.textPreviewUrl || "";
    $("vector").style.width = `${state.prepared.paperWidthMm}mm`;
    notice(state.prepared.textError ? `A/B/C ready. D unavailable: ${state.prepared.textError}` : `Ready. Compare B against D (${state.prepared.textRuns} positioned text runs) at 72 mm.`);
    log(`Prepared ${state.prepared.id.slice(0, 8)} · ${state.prepared.width} × ${state.prepared.height} px · ${state.prepared.paperWidthMm} mm print width. No print submitted.`);
  } catch (error) { notice(error.message, true); log(error.message); }
  finally { state.busy = false; controls(); }
}
async function connectLodop() {
  if (state.loading) return state.loading;
  $("connection").textContent = "Connecting to C-Lodop…";
  state.loading = (async () => {
    try {
      if (!window.getCLodop) {
        for (const port of [8000, 18000]) {
          try {
            await new Promise((resolve, reject) => {
              const script = document.createElement("script");
              const timeout = setTimeout(() => { script.remove(); reject(new Error("C-Lodop connection timed out.")); }, 5000);
              script.src = `http://localhost:${port}/CLodopfuncs.js`;
              script.onload = () => { clearTimeout(timeout); resolve(); };
              script.onerror = () => { clearTimeout(timeout); script.remove(); reject(new Error("C-Lodop script unavailable.")); };
              document.head.appendChild(script);
            });
            break;
          } catch (error) { log(error.message); }
        }
      }
      for (let i = 0; i < 50; i++) {
        if (window.getCLodop) {
          const candidate = window.getCLodop();
          if (candidate && candidate.CVERSION && typeof candidate.ADD_PRINT_HTM === "function") {
            state.lodop = candidate;
            $("connection").textContent = `C-Lodop ${candidate.CVERSION} connected`;
            controls(); return;
          }
        }
        await delay(100);
      }
      throw new Error("C-Lodop unavailable. Start the installed C-Lodop service, then reconnect.");
    } catch (error) {
      state.lodop = null; $("connection").textContent = "C-Lodop not connected"; log(error.message); controls();
    } finally { state.loading = null; }
  })();
  return state.loading;
}
async function printOwn(method) {
  if (state.busy || !state.prepared) return;
  if (method === "d" && !state.prepared.textPreviewUrl) return;
  state.busy = true; controls(); notice(`Submitting ${method.toUpperCase()} to the selected printer…`);
  try {
    const result = await api("/api/print", { id: state.prepared.id, printer: $("printer").value, method, requestId: crypto.randomUUID().replaceAll("-", "") });
    notice(result.message); log(result.message);
  } catch (error) {
    notice(`${error.message} Delivery may be uncertain; check the printer before printing another copy.`, true); log(`${method.toUpperCase()}: ${error.message}`);
  } finally { state.busy = false; controls(); }
}
async function lodopAction(mode, preview) {
  if (state.busy || !state.prepared || !state.lodop) return;
  state.busy = true; controls();
  const lodop = state.lodop;
  const label = mode.toUpperCase();
  notice(preview ? `Opening C-Lodop preview ${label}…` : `Submitting ${label} to C-Lodop…`);
  try {
    const width = state.prepared.paperWidthMm;
    // Provide ample room for this single receipt, including differences in IE font metrics.
    const height = Math.ceil(Math.max(260, state.prepared.heightMm + 40));
    lodop.PRINT_INIT(`ENTREE Compare ${label} ${state.prepared.id.slice(0, 8)}`);
    if (lodop.SET_PRINTER_INDEX($("printer").value) === false) throw new Error("C-Lodop could not select this Windows printer.");
    lodop.SET_PRINT_COPIES(1);
    lodop.SET_PRINT_PAGESIZE(1, `${width}mm`, `${height}mm`, "");
    lodop.SET_PRINT_MODE("POS_BASEON_PAPER", true);
    if (mode === "b") lodop.ADD_PRINT_HTM(0, 0, `${width}mm`, `${height - 2}mm`, state.html);
    else lodop.ADD_PRINT_HTML(0, 0, `${width}mm`, `${height - 2}mm`, state.html);
    const returned = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        lodop.On_Return = null;
        reject(new Error("C-Lodop has not returned a result. Close any preview window and check the printer before continuing."));
      }, preview ? 300000 : 60000);
      if (preview) {
        $("clodop-title").textContent = `C-Lodop preview ${label} · ${$("printer").value}`;
        $("clodop-dialog").showModal();
        state.closePreview = () => {
          clearTimeout(timer); lodop.On_Return = null;
          $("clodop-preview").src = "about:blank";
          $("clodop-dialog").close(); state.closePreview = null;
          resolve("preview closed");
        };
      }
      lodop.On_Return = (taskId, value) => {
        clearTimeout(timer); lodop.On_Return = null;
        log(`${label} ${preview ? "preview closed" : "submission returned"}: ${String(value)} (task ${taskId}).`);
        resolve(value);
      };
      try { if (preview) lodop.PREVIEW("clodop-preview"); else lodop.PRINT(); }
      catch (error) { clearTimeout(timer); lodop.On_Return = null; reject(error); }
    });
    if (!preview && (returned === false || returned === 0 || returned === "false" || returned === "0"))
      throw new Error("C-Lodop reported an unsuccessful submission. Check printer selection and C-Lodop messages.");
    notice(preview ? (returned === "preview closed" ? `Preview ${label} closed. Ready to compare or print another method.` : `Preview ${label} is available. Close it to compare another method.`) : `${label}: C-Lodop returned ${String(returned)}. Check the actual ticket; the callback is not proof of physical completion.`);
  } catch (error) { notice(error.message, true); log(`${label}: ${error.message}`); }
  finally { state.busy = false; controls(); }
}
$("prepare").addEventListener("click", prepare);
$("close-preview").addEventListener("click", () => { if (state.closePreview) state.closePreview(); else $("clodop-dialog").close(); });
$("clodop-dialog").addEventListener("cancel", event => { event.preventDefault(); $("close-preview").click(); });
$("reconnect").addEventListener("click", connectLodop);
$("source-tab").addEventListener("click", () => showPreview(false));
$("image-tab").addEventListener("click", () => showPreview(true));
$("preview-a").addEventListener("click", () => showPreview(true));
$("print-a").addEventListener("click", () => printOwn("a"));
$("print-d").addEventListener("click", () => printOwn("d"));
$("preview-d").addEventListener("click", () => {
  if (!state.prepared?.textPreviewUrl || state.busy) return;
  showPreview(false); $("source").hidden = true; $("vector").hidden = false;
  $("vector").scrollIntoView?.({ behavior: "smooth", block: "start" });
  $("source-tab").setAttribute("aria-pressed", "false");
  notice("D preview uses the prepared text positions. Windows font rendering can differ; compare the physical ticket with B.");
});
for (const mode of ["b", "c"]) {
  $("preview-" + mode).addEventListener("click", () => lodopAction(mode, true));
  $("print-" + mode).addEventListener("click", () => lodopAction(mode, false));
}
$("printer").addEventListener("change", controls);
$("html").addEventListener("input", () => { state.prepared = null; showPreview(false); sourcePreview($("html").value); notice("HTML changed. Prepare comparison to update all four methods."); controls(); });
for (const id of ["paper", "reset"]) $(id).addEventListener(id === "paper" ? "change" : "click", () => { $("html").value = sample(); prepare(); });
$("source").addEventListener("load", () => {
  const doc = $("source").contentDocument;
  if (doc) $("source").style.height = `${Math.min(3500, Math.max(300, doc.body.scrollHeight + 10))}px`;
});
async function init() {
  $("html").value = sample(); sourcePreview($("html").value); controls();
  connectLodop();
  try {
    const session = await api("/api/session"); state.token = session.token;
    $("printer").replaceChildren(new Option("Choose a printer…", ""), ...session.printers.map(name => new Option(name, name)));
    const preferred = session.printers.find(name => /cashier|receipt|kitchen|pos-?80|epson|rongta|xprinter/i.test(name));
    $("printer").value = preferred || session.defaultPrinter || "";
    await prepare();
  } catch (error) { notice(error.message, true); controls(); }
}
init();
