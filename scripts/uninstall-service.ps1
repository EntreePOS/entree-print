param(
    [string]$ServiceName = "EntreePrintPlugin",
    [switch]$RemoveFirewallRules
)

$ErrorActionPreference = "Stop"

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    throw "Run this script from an elevated PowerShell session."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(20))
    }
    sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not remove service (Windows error $LASTEXITCODE)." }
    Write-Host "Removed service $ServiceName."
} else {
    Write-Host "Service $ServiceName is not installed."
}

if ($RemoveFirewallRules) {
    $ruleNames = @('ENTREE Print Plugin HTTP', 'ENTREE Print Plugin TCP SDK', 'ENTREE Print Plugin Discovery')
    Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.DisplayName -in $ruleNames } | Remove-NetFirewallRule -ErrorAction Stop
}
