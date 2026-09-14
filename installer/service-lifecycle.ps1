param(
    [Parameter(Mandatory = $true)][ValidateSet('Check', 'Uninstall')][string]$Action,
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [Parameter(Mandatory = $true)][string]$ErrorFile
)
function Remove-OwnedTrayStartup {
    param([string]$Root)
    $expected = '"' + (Join-Path $Root 'tray\EntreePrintTray.exe') + '" --background'
    foreach ($sid in [Microsoft.Win32.Registry]::Users.GetSubKeyNames()) {
        if ($sid -notmatch '^S-1-(5|12)-[0-9-]+$') { continue }
        $key = [Microsoft.Win32.Registry]::Users.OpenSubKey("$sid\Software\Microsoft\Windows\CurrentVersion\Run", $true)
        if ($null -eq $key) { continue }
        try {
            if ([string]::Equals(($key.GetValue('EntreePrintTray') -as [string]), $expected, [StringComparison]::OrdinalIgnoreCase)) {
                $key.DeleteValue('EntreePrintTray', $false)
            }
        } finally { $key.Dispose() }
    }
}
$ErrorActionPreference = 'Stop'
try {
    $root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
    $ancestor = $root
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'The installation path contains a link. Choose a regular local directory.'
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    $expectedCommand = '"' + (Join-Path $root 'service\EntreePrintPlugin.exe') + '"'
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='EntreePrintPlugin'" -ErrorAction Stop
    if ($service) {
        if (-not [string]::Equals($service.PathName.Trim(), $expectedCommand, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Another Entree Print service uses a different application folder or command. Remove that service through its own installation before continuing.'
        }
        if ($Action -eq 'Check' -and $service.State -ne 'Stopped') {
            throw 'Stop the Entree Print Windows service from its tray menu before updating. Accepted receipts remain saved and resume when you start the service again.'
        }
        if ($Action -eq 'Uninstall') {
            $controller = Get-Service -Name 'EntreePrintPlugin' -ErrorAction Stop
            try {
                if ($controller.Status -ne 'Stopped') {
                    Stop-Service -Name 'EntreePrintPlugin' -ErrorAction Stop
                    $controller.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
                }
            } finally { $controller.Dispose() }
            # Only remove the app's rules after its service ownership was verified.
            $rules = @('ENTREE Print Plugin HTTP', 'ENTREE Print Plugin Discovery', 'ENTREE Print Plugin TCP SDK')
            Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.DisplayName -in $rules } | Remove-NetFirewallRule -ErrorAction Stop
            & "$env:SystemRoot\System32\sc.exe" delete EntreePrintPlugin | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Windows could not remove the service (error $LASTEXITCODE)." }
        }
    }
    if ($Action -eq 'Uninstall') { Remove-OwnedTrayStartup -Root $root }
    # Settings, spool files, service identity and accepted receipts are intentionally
    # outside installer ownership. This helper never deletes or changes them.
    exit 0
} catch {
    [IO.File]::WriteAllText($ErrorFile, $_.Exception.Message, [Text.UTF8Encoding]::new($false))
    exit 1
}
