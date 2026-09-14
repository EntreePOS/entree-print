# Receipt printing comparison

Run from the repository root:

```powershell
.\scripts\start-print-comparison.ps1
```

Open [the local demo](http://127.0.0.1:19779). Requires .NET 10.0.12 or a later 10.0 patch on Windows and installed Edge/Chrome. C-Lodop must be running on localhost:8000 or localhost:18000 for B and C. This is an isolated demo host; it does not change or restart the installed ENTREE or C-Lodop services.

1. Choose the same Windows printer for all methods.
2. Match **print width** to the driver, not the roll label. On the inspected cashier configuration, C-Lodop reported 72.1 mm despite an 80 mm request; the sample therefore starts at 72 mm. Other drivers may need different settings. 48 mm is provided for narrow layouts; 58 and 80 mm are available when the driver actually supports those widths.
3. Prepare comparison. Loading/preparing generates A's PNG and D's text layout but does not print.
4. Inspect HTML, the actual PNG, D's text preview, and the two embedded C-Lodop previews.
5. Compare **B against D at 72 mm** first. Press their Print buttons separately; each requests one copy. Mark the backs. A and C remain available for reference.
6. Measure the ruler (50 mm; 30 mm for the 48 mm sample), compare Chinese strokes, small text, prices, line wrapping, and the END OF RECEIPT marker.

| Method | Actual implementation |
| --- | --- |
| A | Existing `WindowsPrinterBackend` in render-only mode, followed by existing `WindowsImagePrinter` for the displayed PNG. The demo fits the viewport to print width and uses render scale 2; it does not reuse the installed service's possibly different configuration. |
| B | Installed C-Lodop `ADD_PRINT_HTM` with the frozen HTML. |
| C | Installed C-Lodop `ADD_PRINT_HTML` with the same HTML. Vector-snapshot eligibility and final driver commands are not proven by this demo. |
| D | Our Chromium layout extractor produces positioned Unicode text and solid rectangles. `WindowsTextPrinter` draws each text run through GDI+ on the Windows printer graphics context. It does not print a receipt-sized screenshot or call C-Lodop. |

B/C use a fixed page with extra vertical room for engine/font differences. A requests the cropped image's height. Driver forms may override requested dimensions or add trailing blank paper. Compare content quality and the ruler, not blank tail length. C-Lodop's own preview may display images even if the eventual printing path preserves vectors. It may also apply licensing marks. Do not infer native ESC/POS text from a sharp preview.

The editor sends a complete document to both local rendering engines. Use trusted, self-contained, script-free receipt HTML; the demo is not a general HTML sanitization service. Images are in `%LOCALAPPDATA%\EntreePrintComparison\<session>`. Builds also stay under that directory. The host listens only on loopback, requires a session header for mutations, and has no CORS permission for other origins.

Printing is explicit and has no automatic retry. A/D submission IDs are deduplicated only while this demo process runs; reuse with a different method, receipt or printer is rejected. The result `submitted` or a C-Lodop callback is not physical completion confirmation. If delivery is uncertain, check the printer before requesting another copy. Production queue reliability changes from the roadmap are not implemented here.

## D prototype and programmer API

This is an isolated comparison path, not yet the production backend default. It supports self-contained horizontal left-to-right text, ordinary table layout, opaque backgrounds and solid rectangular borders. Chinese and accented Latin are included in the sample. Scripts, external resources, images, SVG, canvas, generated content, RTL/vertical text, transformations and several other unsupported styles disable D with an explanation. Full browser painting compatibility, mixed-font glyph shaping, emojis, complex scripts, overlapping layers and collapsed table borders need further work. Do not use it as an arbitrary HTML replacement yet.

Coordinates use CSS pixels (96 per inch); layout width is explicit. The extractor measures grapheme ranges, groups visual lines, retains computed styling, then resolves a local font that covers each run. The same immutable display list drives SVG preview and Windows drawing. GDI text width is adjusted per run to Chromium's measured extent; SVG preview is an approximation of printer typography, not proof of physical quality. The Windows driver ultimately decides device output. Native ESC/POS is not involved.

Preparation writes `layout.json`, `preview.svg`, and `receipt.emf` under `%LOCALAPPDATA%\EntreePrintComparison\<session>\<receipt-id>`. EMF tests verify text/rule records with no image-drawing records. D checks the driver's available page size and refuses overflow instead of shrinking the receipt. Test the ruler and footer on paper before deciding whether D matches B.

The current demo API freezes the receipt before printing:

```javascript
// Pass X-Demo-Session from GET /api/session on both POST requests.
// POST /api/prepare
{ html: receiptHtml, paperWidthMm: 72 }
// Returns id, textPreviewUrl, textRuns, textError (null when D is available).

// POST /api/print -- only after the user explicitly requests a copy.
{ id: preparedId, printer: "cashier", method: "d", requestId: uniqueId }
// requestId must be a GUID with 32 hexadecimal characters, without dashes.
// Keep the same requestId if checking/repeating the same submission within this process.
```

Historical comparison only: production now uses saved positioned text/vector receipts with durable jobs and tracked Windows handoff. The A screenshot renderer, image printer and HTML patcher are isolated in this sample; the service no longer contains an image printing fallback. The moved screenshot code has been compiled but its current runtime output has not been revalidated. D in this sample still uses the historical custom-page path, so it does not prove the production driver-default page behavior. See the root beta checklist for current release gates.

Validation:

```powershell
dotnet build samples/PrintComparison/PrintComparison.csproj --artifacts-path "$env:LOCALAPPDATA\EntreePrintComparison\build"
node --test samples/PrintComparison/app.test.cjs
node samples/PrintComparison/verify-text-layout.cjs # demo host must be running; never prints
dotnet test EntreePrintPlugin.Tests/EntreePrintPlugin.Tests.csproj --artifacts-path "$env:TEMP\EntreeTextPrototypeTests"
```

References: [C-Lodop HTML graphics example](https://www.lodop.net/demolist/PrintSample44.html), [embedded previews](https://www.lodop.net/faq/pp28.html), [printer selection](https://www.lodop.net/demolist/PrintSample7.html).

D references: [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/), [Windows PrintPage drawing](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.printing.printdocument.printpage).
