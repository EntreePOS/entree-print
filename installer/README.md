# Windows installer

The installer is a development artifact, not a published release. It wraps a verified, self-contained Release package, so .NET installation is not required on the client. Windows printer drivers, Edge/Chrome and fonts are still external prerequisites.

## Build

Use the pinned .NET SDK and [Inno Setup 7.1.0](https://jrsoftware.org/isdl.php). The Inno Setup installer used for the initial build had a valid Pyrsys B.V. Authenticode signature and SHA-256 `0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f`. Its bundled license permits commercial use; the authors request commercial users purchase a license to support development. No license purchase or code-signing credential is supplied by this repository.

```powershell
.\scripts\publish.ps1 -SelfContained -OutputRoot .\dist\my-installer-package
.\scripts\build-installer.ps1 -PackageRoot .\dist\my-installer-package `
  -CompilerPath 'C:\Path\To\Inno Setup 7\ISCC.exe' `
  -OutputRoot .\dist\my-installer
pwsh -NoProfile -File .\scripts\test-installer.ps1
```

Both output directories must be new. The build rejects a framework-dependent or Debug package and verifies every package file before compiling. Outputs are the setup EXE, `SHA256SUMS.txt` and `build-info.json`, which records the input package manifest digest and whether the installer has a valid signature. The current installer is unsigned; hashes alone are not proof of publisher identity.

## Installation lifecycle

- Administrative installation into Program Files, Start menu entries, optional desktop shortcut, and an optional unelevated Settings launch at completion. Silent setup does not launch the tray or service. Settings owns per-user tray startup and service activation after configuration.
- The installer does not create credentials, overwrite saved settings, open firewall ports or automatically activate printing.
- Updates require the service to be stopped and the tray exited. An existing service must have the exact quoted executable path belonging to this installation. A different installation or a modified service command blocks setup. No running service is killed to get past this check.
- Uninstall checks the same ownership, stops that service, removes its named firewall rules and service registration, then permits application-file removal. Failures abort before application files are removed. The uninstall action runs after the normal confirmation and mutex checks; cancellation must not stop the service.
- Matching tray startup entries are removed for loaded Windows user profiles. Entries belonging to another installation are preserved. Users whose registry hives are not loaded should turn off tray startup before uninstalling; offline-profile cleanup remains a verification item.
- Default receipt storage is `%ProgramData%\EntreePrintPlugin\spool`. Settings, identity, deduplication records and receipts are outside the installer file list and retained on uninstall. Explicit custom spool locations are respected. No migration of prototype spool directories is performed.

The lifecycle hook ordering and fatal error behavior were checked against Inno Setup 7.1.0's [uninstaller source](https://github.com/jrsoftware/issrc/blob/is-7_1_0/Projects/Src/Setup.Uninstall.pas). The helper refuses linked installation paths. Its nine child-shell tests replace all Windows service, firewall and startup-registry operations and cover missing/stopped/running/foreign services plus stop, wait, firewall and deletion failures. Those tests do not prove actual installation or removal.

## Remaining installer release checks

1. Clean Windows installation with no .NET runtime; confirm both packaged applications start.
2. Configure and activate the service, verify printer/Chromium access under its account, and review configuration-directory and executable permissions. The current service setup uses LocalSystem; writable configuration that controls executable paths must be addressed before rollout.
3. Verify notification-area icon, Start menu/desktop links and startup for the actual cashier account, including elevation with another administrator's credentials.
4. Exercise cancelled uninstall, busy-service rejection, service-stop failure, upgrade, uninstall and reinstall with retained waiting and uncertain receipts. Verify registry/firewall ownership and data retention on disk.
5. Verify unsigned-installer Windows prompts or supply release signing; complete network/printing gates before uploading an installer to a GitHub release.

An existing prototype service was observed running on the development host. The real read-only ownership check rejected its different installation path and confirmed the same service process remained running. It was not stopped, upgraded or removed to run an installer pilot. No Windows Sandbox installation was available on that host.
