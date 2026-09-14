# Settings layout preview

Run `dotnet run --project samples/SettingsPreview/SettingsPreview.csproj` to inspect the production Settings form with synthetic defaults, without starting the tray, Windows service or a printer job. Closing the window exits the preview.

The form starts with no v2 token. Save writes only `%TEMP%\EntreeSettingsPreview\settings.json`; it still uses the production startup-checkbox handler, so do not save when doing a read-only layout review. No service-restart handler is attached. This harness does not verify real service configuration or installer behavior.
