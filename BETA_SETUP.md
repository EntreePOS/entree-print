# ENTREE Print 0.0.1-beta — development package

This package is for development verification. Release gates in [BETA_RELEASE_CHECKLIST.md](BETA_RELEASE_CHECKLIST.md) remain open. It has not been approved for a live cashier or kitchen rollout.

## Check the package

Extract or copy the whole package into a new directory. Keep `service`, `tray` and `sdk` together. From that directory run:

```powershell
powershell -NoProfile -File .\scripts\verify-package.ps1 -PackageRoot .
```

The check covers the service, tray, SDK, scripts and documents. Missing, changed and extra files fail verification. Run it before starting the service: runtime spool files are intentionally not part of a release package. The SHA-256 manifest detects corruption relative to the supplied manifest; it is not a publisher signature. Do not distribute a package containing settings, access tokens or saved customer tickets.

## Prerequisites and setup sequence

This build targets Windows x64 and .NET 10 LTS. Check `selfContained` in the package manifest: `true` bundles .NET **10.0.12** with both applications; `false` needs .NET, ASP.NET Core and Windows Desktop **10.0.12 or a later 10.0 patch** on the host. For a framework-dependent package, install the x64 Desktop Runtime and ASP.NET Core Runtime from [Microsoft's .NET 10 download page](https://dotnet.microsoft.com/en-us/download/dotnet/10.0); `dotnet --list-runtimes` must list all three families. Existing .NET 9 installations do not satisfy this requirement. Chromium rendering needs a locally installed Microsoft Edge or Google Chrome, accessible to the service account. Chinese and other Unicode text need appropriate installed fonts. Clean-host installation and service-account access still require pilot verification.

Microsoft lists .NET 10 LTS support through November 14, 2028; keep runtime patches current. Use a Windows release covered by the [.NET supported OS policy](https://github.com/dotnet/core/blob/main/os-lifecycle-policy.md). [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

1. Install the printers in Windows and set their paper size and printing preferences there. Verify that the service account can access those queues and driver settings. Keep the cashier's USB queue on its attached computer.
2. Place the complete package in its intended permanent location before installing the service. Default receipt data is stored in `%ProgramData%\EntreePrintPlugin\spool`, outside the application folder. Explicit `SpoolPath` settings override that location. Moving or deleting the ledger loses local job ownership/history. Do not merge a prototype spool into a release package; stop and reconcile the original installation first.
3. Open `tray\EntreePrintTray.exe`. A manual launch opens Settings; the printer icon also appears in the notification area. Windows may put it under the taskbar's hidden-icons arrow. Opening the EXE again should activate the existing Settings window. The `--background` option runs without opening Settings.
4. In Settings, generate an API access token, enter your application's website address if using a browser, and save. Settings are stored in `C:\ProgramData\EntreePrintPlugin\entree-print-settings.json`; installed saves use a fixed elevated helper and require Windows administrator approval. The installer creates administrator-owned configuration and receipt directories. Unsafe existing directories are rejected without adopting or resetting their contents. First launch displays the icon without writing configuration or automatically installing a service before settings are saved. The saved install/start preference can then enable automatic service setup. Service installation/start/restart also requires administrator elevation. Do this only on the designated development host until rollout gates pass.
5. Use the tray's **Windows Service** menu to install/start the packaged service. **Diagnostics** checks service and printer inventory. Enable tray startup from Settings for the signed-in user's logon. A Windows service runs separately from the visible tray.
6. Bundle `sdk` into the POS and follow [sdk/README.md](sdk/README.md). The service does not serve executable SDK JavaScript. Keep the token in the POS's deployment configuration.

HTTP is the default. Optional HTTPS uses a Windows certificate on the same configured port; see below. Installed certificate access and browser/tablet deployment remain release gates. Neither the checksum nor the website list encrypts traffic.

Windows service startup checks configuration and receipt-folder ownership/ACLs before opening the API. Executable paths, including a browser override, must be administrator-protected; user-writable portable executables are rejected. Local Windows users can read configuration and its API token, while receipt files have administrator/SYSTEM-only filesystem permissions. Actual UAC and service-account permission pilots remain required. Development packages in a user-writable source/output folder are not suitable service executables; use the installed Program Files location.

## HTTPS setup

1. Obtain a server certificate from an issuer trusted by your client computers/tablets. Its Subject Alternative Names must cover the DNS name or IP used to connect. Native discovery uses the actual sender IP and requires an IP SAN for that address; the playground's automatic localhost check requires an IP SAN for `127.0.0.1`.
2. On the service computer, import the certificate and private key into **Certificates (Local Computer) → Personal** using Windows certificate management. Grant the actual Windows service account read access to that private key. The installer does not create certificates, install a trusted issuer or change private-key permissions.
3. In tray **Settings → Advanced**, enable **Use HTTPS**, enter the certificate host and its Windows thumbprint, then save and restart the service. The host contains no scheme, port or path and must resolve to this computer for tray diagnostics. The listener still follows the configured bind address. HTTPS replaces HTTP on the configured plugin port; it does not add a second listener.
4. Connect using `protocol: 'https'` and the matching name/IP, as shown in [sdk/README.md](sdk/README.md#https-connections). Retain the API token and permitted website configuration. Verify diagnostics and an authenticated client connection before using it for receipts.

The settings keys are `HttpsCertificateThumbprint` and `HttpsHost`; service environment overrides are `HTTPS_CERTIFICATE_THUMBPRINT` and `HTTPS_HOST`. Both empty means HTTP. Both are required for HTTPS. The service rejects a missing/non-unique certificate, missing private key, invalid dates, host mismatch or incompatible certificate usage. It never falls back to HTTP when HTTPS startup fails. Client trust and private-key access are ultimately checked by the TLS connection; merely finding a certificate does not prove a working deployment.

Renew certificates before expiry. If renewal changes the thumbprint, update the setting and restart; automatic certificate renewal/reload is not implemented. **Reset Default** preserves HTTPS settings and the access token. To disable HTTPS deliberately, clear **Use HTTPS**, save and restart, then change clients to HTTP as appropriate.

Automated tests exercise encrypted Kestrel requests through memory streams, wrong-host and untrusted-issuer rejection, and configuration/discovery behavior. They do not install or trust certificates on this computer or prove an installed service/tablet connection.

## Jobs and removal

Finished receipt layouts are kept for seven days after completion by default. `ReceiptRetentionDays` in the service settings (or `RECEIPT_RETENTION_DAYS`) accepts 1–365; the tray preserves this service-only setting. Pending and uncertain jobs do not expire. Cleanup retains compact job/identity records so delayed retries cannot create another receipt. Expired layouts cannot be previewed or reprinted from history. Admission limits are 100,000 total intents, 10,000 prepared receipts and a 512 MB ledger byte budget; accepted-job status updates can grow beyond that budget. New work returns `QUEUE_FULL` at capacity. Do not delete the ledger to clear that error: doing so removes duplicate protection and ownership. Automatic long-term history export/removal remains unimplemented.

Accepted jobs are persisted before the POS receives acknowledgement. Each destination waits independently when its printer is offline. After Windows accepts a job, the plugin monitors that job instead of sending another copy. “Completed by Windows” is not independent confirmation that paper came out. See [implementation status](API_V2_IMPLEMENTATION.md) for exact current behavior and unimplemented API proposals.

To remove the service, use **Windows Service → Uninstall Service** in the tray, or run `scripts\uninstall-service.ps1 -RemoveFirewallRules` in an elevated PowerShell. Turn off tray startup in Settings before exiting the tray. These steps do not erase saved settings, service identity or tickets. Preserve the ledger while any job is waiting or uncertain. File removal, existing-install upgrades, logon behavior and full clean-host installation still require pilot verification.

## Build from source

Install the x64 **.NET SDK 10.0.401** (or a later patch in that SDK feature band). `global.json` pins the SDK and `Directory.Build.props` supplies the shared target framework/runtime baseline for all projects. Node.js 22.13+ with `node:sqlite` enabled must be on PATH for the SDK/service integration tests. An isolated SDK can also be used by setting `DOTNET_ROOT` and prepending its directory to `PATH` in the build shell.

```powershell
powershell -NoProfile -File .\scripts\publish.ps1
```

The default output is `dist\EntreePrint-0.0.1-beta-win-x64`. Publishing requires a new output directory; use `-OutputRoot` for another build. Build intermediates go into a separate temporary directory, or a supplied `-ArtifactsPath`. Publication compiles and verifies files only; it does not install, start, stop or print anything.

Use `-SelfContained` for a package that bundles the runtime. The installer build requires this option. Build instructions and current deployment limits are in the source repository's `installer/README.md`. A development installer has been compiled, but it is not a verified public release.
