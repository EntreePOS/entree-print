# Real installer lifecycle check. Intentionally restricted to a disposable hosted
# Windows runner; never use it to replace an installation on a developer's PC.
param(
    [Parameter(Mandatory = $true)][string]$InstallerDirectory,
    [Parameter(Mandatory = $true)][string]$EvidenceRoot
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'This installation check runs only on a disposable GitHub-hosted Windows runner.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The disposable runner must have administrator rights.'
    }
} finally { $identity.Dispose() }

$installRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Entree Print'
$dataRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'EntreePrintPlugin'
$configPath = Join-Path $dataRoot 'entree-print-settings.json'
$spoolPath = Join-Path $dataRoot 'spool'
$uninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{523F3547-20CD-4F58-A10A-180A7FBBFCF7}_is1'
$startupKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
foreach ($path in @($installRoot, $dataRoot, $uninstallKey)) {
    if (Test-Path -LiteralPath $path) { throw "Runner is not fresh: $path already exists." }
}
if (Get-CimInstance Win32_Service -Filter "Name='EntreePrintPlugin'") { throw 'Runner already has an Entree Print service.' }
if (Get-ItemProperty -LiteralPath $startupKey -Name EntreePrintTray -ErrorAction SilentlyContinue) { throw 'Runner already has tray startup configured.' }

$InstallerDirectory = (Resolve-Path -LiteralPath $InstallerDirectory).Path
$build = Get-Content -LiteralPath (Join-Path $InstallerDirectory 'build-info.json') -Raw | ConvertFrom-Json
$installer = Join-Path $InstallerDirectory 'EntreePrint-0.0.1-beta-win-x64-setup.exe'
if ($build.installer -ne [IO.Path]::GetFileName($installer) -or $build.version -ne '0.0.1-beta' -or
    (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $build.sha256) { throw 'Installer metadata/digest mismatch.' }
New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
$checks = [Collections.Generic.List[string]]::new()
function Assert-Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw $Description }
    $checks.Add($Description); Write-Host "PASS $Description"
}
function Invoke-InstallerProcess([string]$Path, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Path)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.WorkingDirectory = [Environment]::SystemDirectory
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(180000)) { $process.Kill($true); throw "Installer/helper timed out: $Path" }
        Assert-Check ($process.ExitCode -eq 0) "Process completed: $([IO.Path]::GetFileName($Path)) (exit $($process.ExitCode))"
    } finally { $process.Dispose() }
}
function Assert-ProtectedTree([string]$Path, [bool]$PrivateData = $false) {
    $trusted = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $mutable = [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    $items = @((Get-Item -LiteralPath $Path -Force)) + @(Get-ChildItem -LiteralPath $Path -Recurse -Force)
    foreach ($item in $items) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Installed data contains a link: $($item.FullName)" }
        $acl = Get-Acl -LiteralPath $item.FullName
        if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { throw "Untrusted installed owner: $($item.FullName)" }
        $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($acl.GetSecurityDescriptorBinaryForm(), 0)
        if ($null -eq $descriptor.DiscretionaryAcl) { throw "Missing DACL: $($item.FullName)" }
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -ne 'Allow' -or $rule.IdentityReference.Value -in $trusted -or
                ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly)) { continue }
            if ($PrivateData -or ($rule.FileSystemRights -band $mutable)) { throw "Untrusted installed access: $($item.FullName)" }
        }
    }
    $checks.Add("Protected tree verified ($($items.Count) entries): $Path")
}
function Assert-NoActivation {
    Assert-Check (-not (Get-CimInstance Win32_Service -Filter "Name='EntreePrintPlugin'")) 'Silent setup did not register or start printing'
    Assert-Check (-not (Get-Process EntreePrintTray,EntreePrintPlugin -ErrorAction SilentlyContinue)) 'No interactive tray or service process remains'
}
function Install-Candidate([string]$Phase) {
    Invoke-InstallerProcess $installer @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/TASKS=desktopicon', "/LOG=$(Join-Path $EvidenceRoot ($Phase + '.log'))")
    Assert-Check (Test-Path -LiteralPath $uninstallKey) 'Installer registered its uninstall entry'
    Assert-Check (Test-Path -LiteralPath (Join-Path $installRoot 'tray\EntreePrintTray.exe')) 'Installed tray executable exists'
    Assert-Check (Test-Path -LiteralPath (Join-Path $installRoot 'service\EntreePrintPlugin.exe')) 'Installed service executable exists'
    Assert-ProtectedTree $installRoot
    Assert-ProtectedTree $spoolPath $true
    Assert-NoActivation
}
function Uninstall-Candidate([string]$Phase) {
    Invoke-InstallerProcess (Join-Path $installRoot 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$(Join-Path $EvidenceRoot ($Phase + '.log'))")
    Assert-Check (-not (Test-Path -LiteralPath $uninstallKey)) 'Uninstall registration removed'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $installRoot 'tray\EntreePrintTray.exe'))) 'Tray binary removed by uninstaller'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $installRoot 'service\EntreePrintPlugin.exe'))) 'Service binary removed by uninstaller'
    Assert-NoActivation
}
function Assert-RetainedData {
    foreach ($path in $retained.Keys) {
        Assert-Check ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $retained[$path]) "Retained identical bytes: $([IO.Path]::GetFileName($path))"
    }
    Assert-ProtectedTree $spoolPath $true
}

try {
    Install-Candidate '01-install'
    Assert-Check (-not (Test-Path -LiteralPath $configPath)) 'Installer did not create credentials or unsaved default configuration'
    $shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Entree Print\Entree Print.lnk'
    Assert-Check (Test-Path -LiteralPath $shortcut) 'Start menu shortcut exists'
    $desktop = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Entree Print.lnk'
    Assert-Check (Test-Path -LiteralPath $desktop) 'Selected desktop shortcut exists'

    $request = Join-Path $EvidenceRoot 'synthetic-settings-request.json'
    @{ AccessToken = 'installer-ci-synthetic-token'; AutoInstallWindowsService = $false; BindAddress = '127.0.0.1' } |
        ConvertTo-Json | Set-Content -LiteralPath $request -Encoding utf8
    Invoke-InstallerProcess (Join-Path $installRoot 'tray\EntreePrintTray.exe') @('--save-settings', $request)
    Assert-Check ((Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).AccessToken -eq 'installer-ci-synthetic-token') 'Installed fixed settings helper saved the supplied data'
    Assert-ProtectedTree $dataRoot
    # These are byte-retention fixtures, not accepted ledger jobs or recovery proof.
    $waiting = Join-Path $spoolPath 'ci-waiting.json'
    $uncertain = Join-Path $spoolPath 'ci-uncertain.json'
    [IO.File]::WriteAllText($waiting, '{"fixture":"waiting","text":"厨房 receipt"}')
    [IO.File]::WriteAllText($uncertain, '{"fixture":"uncertain","idempotencyKey":"ci-original-key"}')
    $retained = @{}
    foreach ($path in @($configPath, $waiting, $uncertain)) { $retained[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    Install-Candidate '02-update'
    Assert-RetainedData

    if (-not (Test-Path -LiteralPath $startupKey)) { New-Item -Path $startupKey | Out-Null }
    New-ItemProperty -LiteralPath $startupKey -Name EntreePrintTray -PropertyType String -Value ('"' + (Join-Path $installRoot 'tray\EntreePrintTray.exe') + '" --background') | Out-Null
    Uninstall-Candidate '03-uninstall'
    Assert-RetainedData
    Assert-Check (-not (Get-ItemProperty -LiteralPath $startupKey -Name EntreePrintTray -ErrorAction SilentlyContinue)) 'Matching loaded-user tray startup was removed'
    Assert-Check (-not (Test-Path -LiteralPath $shortcut)) 'Start menu shortcut removed'
    Assert-Check (-not (Test-Path -LiteralPath $desktop)) 'Desktop shortcut removed'

    Install-Candidate '04-reinstall'
    Assert-RetainedData
    Uninstall-Candidate '05-final-uninstall'
    Assert-RetainedData
    @{ ok = $true; sourceCommit = $env:GITHUB_SHA; installerSha256 = $build.sha256; checks = @($checks); serviceActivated = $false;
        scope = 'Silent administrator installer lifecycle on a hosted Windows VM. UAC prompts, interactive logon, service startup and printer recovery are not tested.' } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'result.json') -Encoding utf8
} catch {
    @{ ok = $false; sourceCommit = $env:GITHUB_SHA; error = $_.Exception.Message; checks = @($checks) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'result.json') -Encoding utf8
    foreach ($path in @($installRoot, $dataRoot, $spoolPath)) {
        if (Test-Path -LiteralPath $path) { Get-Acl -LiteralPath $path | Format-List Path,Owner,Sddl | Out-File -LiteralPath (Join-Path $EvidenceRoot 'permissions.txt') -Append }
    }
    throw
}
