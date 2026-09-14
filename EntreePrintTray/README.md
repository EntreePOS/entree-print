# ENTREE Print Plugin Tray

WinForms tray app for local plugin configuration.

## Run

```powershell
dotnet run --project EntreePrintTray/EntreePrintTray.csproj
```

Manual launch opens Settings. Launching it again brings Settings back in the existing process. Closing Settings keeps the notification icon running; Exit in the icon menu closes the tray process. Windows may place the icon in its notification-area overflow (the up arrow near the clock).

`EntreePrintTray.exe --background` starts only the notification icon. Enable **Tray starts with Windows** in Settings to register background startup for the current user. This is separate from **Install/start print service**: a Windows service runs without a logged-in desktop and cannot supply the user's tray icon.

Both executables embed the printer icon from `assets/entree-print.ico`, with eight sizes from 16 to 256 pixels. Regenerate the asset with `scripts/generate-icon.ps1`. Startup failures are reported in a dialog and logged to `%LOCALAPPDATA%\EntreePrintPlugin\tray-startup.log`.

By default, the tray installs and starts the C# print service as an automatic Windows Service. Windows may show a UAC prompt because service installation requires administrator permission.

## Config File

By default, settings are saved to:

```text
C:\ProgramData\EntreePrintPlugin\entree-print-settings.json
```

Set `ENTREE_PRINT_CONFIG` to override the path for both the tray app and service.

The service reads the same file on startup. After saving settings, the tray prompts to restart the Windows Service if it is installed so runtime changes can apply immediately. If the service is not installed and automatic install is enabled, the tray installs and starts it.

## Windows Service Actions

The tray app installs the service by default. Disable `Install service automatically` if you want manual control.

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

These actions prompt for administrator elevation.

`Diagnostics` calls `/api/health` and refreshes `/api/printers?refresh=true` with the configured API token and verified service ID.

`Install Service` uses the current saved HTTP and UDP discovery ports when creating firewall rules. The SDK uses the HTTP API; there is no prototype WebSocket SDK endpoint.

The install action looks for the service executable in:

```text
..\service\EntreePrintPlugin.exe
```

relative to the tray app folder. Set `ENTREE_PRINT_SERVICE_EXE` to override this path.

## Settings

- HTTP and discovery ports, network accessibility and service/tray startup.
- POS website address and a masked API access token.
- Browser path override, status refresh and safe retry preferences.

Configure paper size, resolution and driver-supported cutting in Windows. Entree Print reads the driver settings when preparing a receipt; customers do not maintain a second printer profile. The added profile editor was removed after the user's correction. Prototype image/PDF engine, pixel viewport, global device-command and raw text-encoding controls are also removed. Optional programmer device-command configuration remains service-side and is not required for normal receipt printing.

Save replaces the configuration atomically after writing and flushing a temporary file. Reset Default preserves the staged token and service-only settings. Generate/Replace token affects the form until Save and service restart. Use the same token in POS configuration. Website addresses are CORS origins such as https://pos.example.com, without page paths; they do not replace authentication.

First launch no longer saves configuration before creating the notification icon. If no settings file exists, the tray asks the user to open Settings and save setup; it does not automatically install/start a service with unsaved defaults. This removes an unnecessary startup write that could fail before the icon appeared. Existing saved auto-start preferences still apply. Fresh-install/permissions and logon UI verification remain required.

181 .NET tests and 42 SDK tests pass. The profile editor was briefly inspected in an isolated preview, then removed and that obsolete preview process closed. No installed service/tray settings or Windows printer preferences were changed. The simplified source UI still needs its final package/layout check.
