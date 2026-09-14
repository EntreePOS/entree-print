# Tray desktop preview

This sample hosts the production tray application context, Settings forms and printer icon using a temporary configuration with automatic service installation disabled. It uses a separate single-instance mutex so it can coexist with an installed tray. Opening the preview EXE again activates the existing preview window. Do not use its Windows Service menu or save startup preferences during visual checks: those controls still invoke real operations.

Publish a self-contained Windows x64 build into a fresh temporary directory when the installed machine runtime is older than the project baseline. Then open `TrayPreview.exe` normally. This verifies the current .NET runtime and tray UI without requiring a machine-wide runtime installation. The wrapper uses the production context and instance guard; it does not prove deployment of `EntreePrintTray.exe`, service setup or logon registration.

Each primary launch writes `%TEMP%\EntreeTrayDesktopPreview\<unique-id>\preview.json` with the process ID, runtime and configuration path. Close the preview using its tray Exit menu. Closing Settings alone keeps the tray running.

## September 14 desktop check

Self-contained .NET 10.0.12 preview PID 60072 opened Settings visibly on its first launch. The narrow Basic layout, short port fields, Reset Default and title-bar printer icon were visually inspected. Closing Settings removed its window while the same PID remained alive; launching the EXE again opened a new Settings window in that same process. No saved settings, startup registration or Windows service were changed during these checks.

Evidence: `%TEMP%\EntreeTrayDesktopPreview\b3dc41a06b1044779e9e3c236295a49e\desktop-check.json` and `preview.json`. The app was left open for the operator to locate its notification-area icon. Notification-area/hidden-icons placement and Windows logon behavior still need confirmation. This desktop check preceded the defensive menu-error change; that change has seven additional regression tests and does not change the visible layout.
