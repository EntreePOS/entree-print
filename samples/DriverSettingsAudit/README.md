# Driver settings inspection

Read current Windows printer defaults without creating a print job:

```powershell
dotnet run --project samples/DriverSettingsAudit
# Or name an installed queue explicitly:
dotnet run --project samples/DriverSettingsAudit -- "Kitchen Printer"
```

The default is the explicitly named Microsoft Print to PDF queue; it never falls back to the default printer. This calls `CreateMeasurementGraphics` and `GetDeviceCaps`, with no StartDoc or device command. Windows documents measurement graphics as a way to inspect printer information without creating a job: [Microsoft reference](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.printing.printersettings.createmeasurementgraphics?view=windowsdesktop-9.0).

Observed in this workspace: Microsoft Print to PDF returned 5100 × 6600 printable/physical dots, 600 × 600 DPI, zero offsets and 215.9 mm printable width. This is evidence for read-only driver measurement under the development user, not for service-account settings, physical output, receipt pagination or cutting. Those require separate verification.

For Windows-only status inventory, run `dotnet run --project samples/DriverSettingsAudit -- --inventory`. This invokes the production inventory query once, without starting its background worker or any HTTP/UDP listener. It does not submit jobs or open direct device sockets. After removal of the ESC/POS probe, the development-user audit returned seven Windows queues (one reported offline), all with fresh observations from Windows. This proves the query executes on this host, not that every printer sensor is supported.
