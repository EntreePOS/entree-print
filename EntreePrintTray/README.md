# ENTREE Print Plugin Tray

WinForms tray app for local plugin configuration.

## Run

```powershell
dotnet run --project EntreePrintTray/EntreePrintTray.csproj
```

Manual launch opens Settings. Launching it again brings Settings back in the existing process. Closing Settings keeps the notification icon running; Exit in the icon menu closes the tray process. Windows may place the icon in its notification-area overflow (the up arrow near the clock).

`EntreePrintTray.exe --background` starts only the notification icon. Enable **Tray starts with Windows** in Settings to register background startup for the current user. This is separate from **Install/start print service**: a Windows service runs without a logged-in desktop and cannot supply the user's tray icon.

Both executables embed the printer icon from `assets/entree-print.ico`, with eight sizes from 16 to 256 pixels. Regenerate the asset with `scripts/generate-icon.ps1`. Startup failures are reported in a dialog and logged to `%LOCALAPPDATA%\EntreePrintPlugin\tray-startup.log`.

On a new installation, opening the tray displays Settings without saving configuration or installing the service. Saving with **Install/start print service** enabled can install/start the Windows service; later tray launches honor that saved setting. Windows may show a UAC prompt because service installation requires administrator permission.

September 14 production launch check: a current self-contained build of the actual tray EXE opened a responsive Settings window (PID 43248), with its controls readable through desktop automation. The existing print service remained running as PID 6252. Window activation failed twice, and the screenshot did not establish visual appearance; operator confirmation of Settings and notification-area placement is pending. The executable and `desktop-check.json` evidence are under `%LOCALAPPDATA%\EntreePrintBuild\tray-422baf8`. This proves process startup only, not installer deployment, visible icon placement or logon startup. A follow-up check during installer run 34844339986 found the same Settings window; Raise and refreshed activation again returned `failed to activate captured window`, and capture still showed wallpaper. No service or settings controls were changed.

## Config File

By default, settings are saved to:

```text
C:\ProgramData\EntreePrintPlugin\entree-print-settings.json
```

Set `ENTREE_PRINT_CONFIG` to override the path for both the tray app and service.

The service reads the same file on startup. After saving settings, the tray prompts to restart the Windows Service if it is installed so runtime changes can apply immediately. If the service is not installed and automatic install is enabled, the tray installs and starts it.

## Windows Service Actions

Disable **Install/start print service** if you want to control the service manually through the tray menu.

Use the tray icon menu:

```text
Ports...
Diagnostics
Windows Service -> Install Service
Windows Service -> Start Service
Windows Service -> Stop Service
Windows Service -> Restart Service
Windows Service -> Uninstall Service
```

These actions prompt for administrator elevation. The installed tray launches itself with a fixed `--service-action` operation. It accepts no script, executable path, configuration path or output path from the requesting process. Service commands are constructed only after elevation; no user-temporary PowerShell script or diagnostic file is executed or written by the elevated helper.

Before changing a service or firewall rule, the helper checks that the Windows service command exactly matches this installation's quoted service executable. A foreign path, unquoted command or extra argument stops the operation. An absent service can be installed; uninstalling an absent service does nothing. Missing services cannot be started, stopped or restarted.

The helper reads saved settings from the protected default ProgramData location, and requires trusted permissions on both executables. It invokes system Windows PowerShell without profiles, with system-only command and module lookup. Operations have a 90-second limit; a timeout requires checking current service status before retrying. Windows/UAC errors remain visible to the operator.

`Diagnostics` calls `/api/health` and refreshes `/api/printers?refresh=true` with the configured API token and verified service ID.

`Install Service` uses the current saved HTTP and UDP discovery ports when creating firewall rules. The SDK uses the HTTP API; there is no prototype WebSocket SDK endpoint.

The install action looks for the service executable in:

```text
..\service\EntreePrintPlugin.exe
```

relative to the tray app folder. Service management ignores `ENTREE_PRINT_SERVICE_EXE` and `ENTREE_PRINT_CONFIG` overrides. Those remain development/diagnostic conveniences, not privileged installation inputs. Install the package into its protected application folder before using the service menu.

## Settings

- HTTP and discovery ports, network accessibility and service/tray startup.
- POS website address and a masked API access token.
- Browser path override, status refresh and safe retry preferences.

Configure paper size, resolution and driver-supported cutting in Windows. Entree Print reads the driver settings when preparing a receipt; customers do not maintain a second printer profile. The added profile editor was removed after the user's correction. Prototype image/PDF engine, pixel viewport, global device-command and raw text-encoding controls are also removed. Optional programmer device-command configuration remains service-side and is not required for normal receipt printing.

Save replaces the configuration atomically after writing and flushing a temporary file. Reset Default preserves the staged token and service-only settings. Generate/Replace token affects the form until Save and service restart. Use the same token in POS configuration. Website addresses are CORS origins such as https://pos.example.com, without page paths; they do not replace authentication.

First launch no longer saves configuration before creating the notification icon. If no settings file exists, the tray asks the user to open Settings and save setup; it does not automatically install/start a service with unsaved defaults. This removes an unnecessary startup write that could fail before the icon appeared. Existing saved auto-start preferences still apply. Fresh-install/permissions and logon UI verification remain required.

181 .NET tests and 42 SDK tests pass. The profile editor was briefly inspected in an isolated preview, then removed and that obsolete preview process closed. No installed service/tray settings or Windows printer preferences were changed. The simplified source UI still needs its final package/layout check.
