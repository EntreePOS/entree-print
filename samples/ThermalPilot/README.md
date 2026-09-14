# Cashier physical pilot

This console submits **one physical receipt to the exact Windows queue `cashier`**. Run only after the operator confirms that queue is connected and ready. It uses production API preparation/validation, the durable ledger, per-printer worker, GDI backend and Windows job monitor. No service listener is started and no driver settings are changed.

Build with the pinned .NET SDK, then run the compiled DLL with `--print-cashier <fresh-output-directory>`. The directory claim prevents rerunning the same pilot. If a response is missing, inspect its ledger and Windows job; do not start another pilot automatically.

The receipt contains Chinese, English, accented text, QR `ENTREE-BETA-001` and Code128 `BETA001`. It adopts Windows printable width/DPI and must fit one driver page. The driver owns feeding/cutting. The directory retains the exact HTML preview, prepared render, intent, ledger and result.

## September 14, 2026 result — operator approved

Operator confirmed `cashier` ready. One production-path job was submitted: Windows job **23**, POS-80C / USB001, 576 printable dots at 203 DPI (72.0709 mm). Content height was 80.6979 mm and pagination produced one page. Windows reported completion with flags 148; the validated request had one retained intent.

The operator answered **“Printed clearly; both codes scan correctly”** and explicitly confirmed satisfaction with clarity. This establishes the approved short-receipt quality baseline for Chinese/English text and QR/Code128 on this printer/driver. Long-receipt feeding/cutting, other printers and failure recovery still need their own checks.

Local evidence: `%TEMP%\EntreeThermalPilot\cashier-beta-20260914-01\result.json`, with `preview.html`, `intent.json` and `jobs` alongside it. Log: `%TEMP%\EntreeThermalPilotCashier.log`. This directly calls production service classes under the development account; it does not verify HTTP/UDP, installed-service permissions, real network failure or another physical printer. The result JSON records machine observations only; physical confirmation is recorded here separately.
