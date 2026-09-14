# Windows installer

The installer is a development artifact, not a published release. It wraps a verified, self-contained Release package, so .NET installation is not required on the client. Windows printer drivers, Edge/Chrome and fonts are still external prerequisites.

## Build

The latest verified candidate is [run 34838741586](https://github.com/EntreePOS/entree-print/actions/runs/34838741586), source `d021c374d627be2309fc4faf48b294fd106ba5cc`, including prepared receipt comparison. All 431 application tests, 115 core SDK tests, nine SQLite tests and 64 hosted installer checkpoints pass. [Exact evidence](evidence/windows-ci-34838741586.json) and PACKAGE_VERIFICATION.md identify the downloaded, hash-verified EXE. Printing is inactive during hosted installation tests; live service, printer/network and tray/logon verification remain open.

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

- Administrative installation into Program Files, Start menu entries, optional desktop shortcut, and an optional unelevated Settings launch at completion. Setup runs the tray executable's fixed storage-initialization command without opening its UI; silent setup does not launch the interactive tray or service. Settings owns per-user tray startup and service activation after configuration.
- The installer does not create credentials, overwrite saved settings, open firewall ports or automatically activate printing.
- Updates require the service to be stopped and the tray exited. An existing service must have the exact quoted executable path belonging to this installation. A different installation or a modified service command blocks setup. No running service is killed to get past this check.
- Uninstall checks the same ownership, stops that service, removes its named firewall rules and service registration, then permits application-file removal. Failures abort before application files are removed. The uninstall action runs after the normal confirmation and mutex checks; cancellation must not stop the service.
- Matching tray startup entries are removed for loaded Windows user profiles. Entries belonging to another installation are preserved. Users whose registry hives are not loaded should turn off tray startup before uninstalling; offline-profile cleanup remains a verification item.
- Default receipt storage is `%ProgramData%\EntreePrintPlugin\spool`. Settings, identity, deduplication records and receipts are outside the installer file list and retained on uninstall. Explicit custom spool locations are respected. No migration of prototype spool directories is performed.
- Initialization creates administrator-owned directories with explicit ACLs. Local Windows users can read configuration (including the API token), but only Administrators and SYSTEM can change it. Receipt storage grants filesystem access only to Administrators and SYSTEM. Local users with the token are trusted API clients; this is not per-user API authorization.
- Saving installed settings requests UAC through the installed tray executable. The helper reads up to 1 MB from one open request handle without write/delete sharing and always writes to the fixed configuration location. Missing, null, malformed and invalid requests fail before saving. It does not accept an output path or script. Temporary request data grants access only to its submitting user, Administrators and SYSTEM. The normal tray remains responsive and controls startup for its original Windows account.
- Service menu actions elevate the installed tray with one fixed operation. The helper ignores caller executable/configuration overrides, constructs its commands after elevation and checks the registered service belongs to its own package before changing a service or firewall rule. User-temporary PowerShell scripts are no longer part of tray service management.
- Unsafe existing owners/ACLs and links are rejected without rewriting their permissions or adopting their data. If initialization fails, setup returns exit code 12 and does not launch Settings automatically; application files may already be installed. Review the existing data with an administrator before retrying.
- A Windows service validates configuration and receipt storage before claiming jobs or opening listeners. Browser executables must have trusted owners and non-administrator write protection, including parent-directory protection. A user-writable portable browser cannot run as the service account.

The lifecycle hook ordering and fatal error behavior were checked against Inno Setup 7.1.0's [uninstaller source](https://github.com/jrsoftware/issrc/blob/is-7_1_0/Projects/Src/Setup.Uninstall.pas). The helper refuses linked installation paths. Its nine child-shell tests replace all Windows service, firewall and startup-registry operations and cover missing/stopped/running/foreign services plus stop, wait, firewall and deletion failures. Those tests do not prove actual installation or removal.

## Hosted installer verification

The manually dispatched `Verify Windows installer` GitHub workflow builds the self-contained candidate, runs the application and isolated lifecycle tests, then runs `scripts/verify-installed-package.ps1` on a fresh `windows-2025` hosted runner. The script refuses developer machines, self-hosted runners and pre-existing Entree Print installations/data. It exercises silent install, same-version update, uninstall and reinstall, installed file permissions, fixed-helper settings save, byte retention, shortcuts and matching loaded-user startup cleanup. Logs, result JSON and the candidate are retained as workflow artifacts for 14 days; this does not create a GitHub Release.

This is real installer execution with printing left inactive. The receipt files are synthetic byte-retention fixtures, not accepted jobs or spooler-recovery evidence. [GitHub's Windows runners](https://docs.github.com/en/actions/reference/runners/github-hosted-runners#administrative-privileges) run as administrators with UAC disabled and contain development runtimes. A passing run does not establish interactive UAC behavior, fresh-user logon, a Windows client with no installed .NET, service-account startup or hardware/network recovery.

The first successful real lifecycle run is [34819567689](https://github.com/EntreePOS/entree-print/actions/runs/34819567689), from `bb1432be8ed81fd06567da864461b12564ea7674`. Its [recorded result](evidence/windows-ci-34819567689.json) contains 64 checkpoints across the lifecycle phases. All 308 application tests also passed. The artifact is an unsigned development candidate, not a published beta release.

The updated candidate from `297a4645b5018f1469886bb30c983ccbbd8dec4b` passes the same lifecycle in [run 34822273890](https://github.com/EntreePOS/entree-print/actions/runs/34822273890), with 319 passing application tests and the corrected settings-request reader. Its [recorded result](evidence/windows-ci-34822273890.json) and PACKAGE_VERIFICATION.md identify the tested installer and independently verified hash. It remains an unsigned development candidate.

## Remaining installer release checks

Previous verified development candidate: [run 34837087581](https://github.com/EntreePOS/entree-print/actions/runs/34837087581), source `4545cd6f777ca5b2537b156a98e20235e9a90e76`, includes Windows destination identity, backup inventory revalidation, retained uncertainty, optional HTTPS, ordered receipt actions, resumable status and native SQLite storage. All 416 application tests, 115 core SDK tests, nine SQLite tests and 64 installer checkpoints pass. [Exact result](evidence/windows-ci-34837087581.json) and PACKAGE_VERIFICATION.md identify its independently verified installer hash and limits. Automatic routing, installed certificate/service access, interactive tray/logon and live printer/network recovery remain open.

1. Clean Windows installation with no .NET runtime; confirm both packaged applications start.
2. Configure and activate the service, verify printer/Chromium access under its account, and pilot the new protected-directory creation and UAC save path with both ordinary and alternate administrator accounts. The service setup still uses LocalSystem. Review every runtime dependency's permissions and verify the fixed installed service-management entry point under UAC before rollout. Its command generation and ownership checks have automated coverage; actual elevated deployment remains unverified.
3. Verify notification-area icon, Start menu/desktop links and startup for the actual cashier account, including elevation with another administrator's credentials.
4. Exercise cancelled uninstall, busy-service rejection, service-stop failure, upgrade, uninstall and reinstall with retained waiting and uncertain receipts. Verify registry/firewall ownership and data retention on disk.
5. Verify unsigned-installer Windows prompts or supply release signing; complete network/printing gates before uploading an installer to a GitHub release.

An existing prototype service was observed running on the development host. The real read-only ownership check rejected its different installation path and confirmed the same service process remained running. It was not stopped, upgraded or removed to run an installer pilot. No Windows Sandbox installation was available on that host.
