# Windows spooler smoke

Submits only to the explicitly named **Microsoft Print to PDF** virtual queue. It never sends a thermal job or chooses the default printer. Requires Windows, the virtual queue, .NET 10.0.12 or a later 10.0 patch, and Edge/Chrome for layout preparation. Source builds use the SDK pinned by the repository's global.json.

```powershell
dotnet run --project samples/BetaSmoke/BetaSmoke.csproj --artifacts-path "$env:TEMP\EntreeSpoolerSmokeBuild" -- "$env:TEMP\EntreeSpoolerSmoke-new-run"
```

Use a fresh directory. Outputs include HTML preview, positioned layout, EMF, PDF, a durable ledger and `result.json`. The fixture contains Chinese, Latin text, a Unicode QR value and Code128 rectangles. The monitor registers before submission; a successful result requires PDF output and either evidenced Windows completion or an explicitly unknown outcome. Inspect `Status`/`WindowsStatus`; `ok` alone does not prove completion.

Observed 2026-09-13 local time: job 11 and concurrent jobs 12/13 all reported `completed`, flags 388 (`PRINTED | DELETING | DELETED`), captured through notifications with distinct attempt names. [Recorded results](observed-spooler-results.json) preserve the outputs. Completion was retained even when deletion was included in the notification. Concurrent processes each tracked their own receipt successfully.

This verifies native layout submission and Windows correlation, not thermal quality, scanner reliability or device paper-out reporting. Those remain release gates.

## Long receipt and current driver-default pages

Add `--long` after the fresh output directory to create a 120-item receipt. The sample reads the explicitly named PDF driver's DPI, prepares codes at that resolution, saves each planned page as SVG, and submits all pages as one Windows job. `result.json` includes expected page count, item count and driver DPI.

The latest output is `output/pdf/long-receipt-pagination-readable-codes` at repository root. Windows job 20 completed with flags 388. Its four 612 × 792 point pages retained items 001–034, 035–074, 075–114 and 115–120. All four 1200-pixel QA renders were visually inspected: no cut lines or missing items; Chinese text and final codes are intact. The earlier `long-receipt-pagination` directory is a superseded barcode-size experiment.

`verify_pdf.py <output-directory>` verifies page count, saved fixture sequence, extractable PDF item order and image-band resolution. The Microsoft driver rasterized 16 individual text bands at 600 DPI; only 109 item strings remain extractable, so this script explicitly requires rendered-page inspection for completeness. It does not claim zero image resources or infer missing ink from unextractable text.

Rasterize the last PDF page at 300 DPI with Poppler, then run `dotnet run --project samples/BetaSmoke -- --verify-codes <png-path>`. This read-only mode decoded both `欢迎 https://entree.example/10086` and `ORDER10086` from the latest PDF. Barcode modules now scale in physical units (minimum about 0.25 mm, preferred about 0.375 mm), rounded to whole driver dots. Before this correction, the old three-dot cap produced overly thin bars at 600 DPI and the virtual-page barcode scan failed.

These samples do not install/start an HTTP or UDP service, submit physical printer jobs, or prove physical cutting/scanner behavior. The continuous HTML preview does not currently show driver page boundaries.

## Dot alignment follow-up

The newer `output/pdf/long-receipt-dot-aligned` result is job 21, completed with flags 388. Code rectangles now preserve encoder dot counts and use a shared snapped origin; page breaks also use the driver dot grid. Chromium's root width uses the exact requested physical width. All four output pages were visually checked with the same item ranges as above, and both codes decoded again from the last page at 300 DPI. Structural verification still reports 109 extractable item strings and 16 native-600-DPI text bands, with the remaining content confirmed in the page renders. Physical hardware validation is still outstanding.
