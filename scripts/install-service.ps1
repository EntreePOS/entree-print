param(
    [string]$InstallRoot = "C:\Program Files\EntreePrintPlugin",
    [string]$ServiceName = "EntreePrintPlugin",
    [string]$DisplayName = "ENTREE Print Plugin",
    [int]$HttpPort = 9779,
    [int]$DiscoveryPort = 9778,
    [string]$BindAddress = "0.0.0.0",
    [switch]$SkipPublish,
    [switch]$SkipVerify
)

$ErrorActionPreference = "Stop"

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Set-EntreeFirewallRules {
    param(
        [int]$HttpPort,
        [int]$DiscoveryPort,
        [string]$BindAddress
    )

    $ruleNames = @('ENTREE Print Plugin HTTP', 'ENTREE Print Plugin TCP SDK', 'ENTREE Print Plugin Discovery')
    Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.DisplayName -in $ruleNames } | Remove-NetFirewallRule -ErrorAction Stop

    $address = $null
    $loopback = $BindAddress -eq 'localhost' -or ([Net.IPAddress]::TryParse($BindAddress, [ref]$address) -and [Net.IPAddress]::IsLoopback($address))
    if (-not $loopback) {
        New-NetFirewallRule -DisplayName 'ENTREE Print Plugin HTTP' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $HttpPort -RemoteAddress LocalSubnet -Profile Any -Enabled True -ErrorAction Stop | Out-Null
        New-NetFirewallRule -DisplayName 'ENTREE Print Plugin Discovery' -Direction Inbound -Action Allow -Protocol UDP -LocalPort $DiscoveryPort -RemoteAddress LocalSubnet -Profile Any -Enabled True -ErrorAction Stop | Out-Null
    }
}

if (-not (Test-Admin)) {
    throw "Run this script from an elevated PowerShell session."
}

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot "publish.ps1") -OutputRoot $InstallRoot
}

$exePath = Join-Path $InstallRoot "service\EntreePrintPlugin.exe"
if (-not (Test-Path $exePath)) {
    throw "Service executable not found: $exePath"
}

if (-not $SkipVerify) {
    & (Join-Path $PSScriptRoot "verify-package.ps1") -PackageRoot $InstallRoot | Out-Host
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(20))
    }
    sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not remove service (Windows error $LASTEXITCODE)." }
    Start-Sleep -Seconds 2
}

New-Service -Name $ServiceName -BinaryPathName ('"' + $exePath + '"') -StartupType Automatic -DisplayName $DisplayName -ErrorAction Stop | Out-Null
sc.exe description $ServiceName "Local LAN HTML print plugin for ENTREE POS tablet clients." | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not configure service (Windows error $LASTEXITCODE)." }

Set-EntreeFirewallRules -HttpPort $HttpPort -DiscoveryPort $DiscoveryPort -BindAddress $BindAddress

Start-Service -Name $ServiceName -ErrorAction Stop
(Get-Service -Name $ServiceName -ErrorAction Stop).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
Write-Host "Installed and started $DisplayName ($ServiceName)."
