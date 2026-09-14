# Exercises package verification with disposable synthetic files. Never installs or launches an app.
$ErrorActionPreference = 'Stop'
$verify = Join-Path $PSScriptRoot 'verify-package.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('EntreePackageTests-' + [Guid]::NewGuid().ToString('N'))
$required = @(
    'service/EntreePrintPlugin.exe', 'service/EntreePrintPlugin.dll', 'service/EntreePrintPlugin.runtimeconfig.json', 'service/appsettings.json',
    'tray/EntreePrintTray.exe', 'tray/EntreePrintTray.dll', 'tray/EntreePrintTray.runtimeconfig.json',
    'sdk/entree-print.mjs', 'sdk/outbox.mjs', 'sdk/events.mjs', 'sdk/native.mjs', 'sdk/native-discovery.mjs', 'sdk/sqlite-outbox.mjs', 'sdk/package.json', 'sdk/README.md',
    'scripts/verify-package.ps1', 'scripts/uninstall-service.ps1',
    'README.md', 'BETA_SETUP.md', 'API_DESIGN.md', 'API_V2_IMPLEMENTATION.md', 'BETA_RELEASE_CHECKLIST.md'
)
$script:passed = 0
function Check-Package {
    param([string]$Name, [scriptblock]$Mutate, [string]$ExpectedError = '')
    $root = Join-Path $testRoot $Name
    $files = @(foreach ($relative in $required) {
        $path = Join-Path $root $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        [IO.File]::WriteAllText($path, 'fixture')
        [pscustomobject]@{ path = $relative; bytes = 7; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    })
    $manifest = [pscustomobject]@{
        schema = 'entree-print-package-0.0.1'; version = '0.0.1-beta'; runtime = 'win-x64'
        algorithm = 'sha256'; configuration = 'Release'; selfContained = $false; files = $files
    }
    & $Mutate $root $manifest
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'manifest.sha256.json') -Encoding UTF8
    $caught = $null
    try { $result = & $verify -PackageRoot $root | ConvertFrom-Json }
    catch { $caught = $_.Exception.Message }
    if ($ExpectedError) {
        if (-not $caught -or $caught -notlike "$ExpectedError*") { throw "$Name expected '$ExpectedError'; received '$caught'." }
    } elseif ($caught -or -not $result.ok -or $result.files -ne $required.Count) { throw "$Name failed: $caught" }
    $script:passed++
    Write-Host "PASS $Name"
}
Check-Package valid { }
Check-Package hash { param($root) [IO.File]::WriteAllText((Join-Path $root 'sdk/native.mjs'), 'changed') } 'Hash mismatch'
Check-Package size { param($root) [IO.File]::WriteAllText((Join-Path $root 'sdk/native.mjs'), 'longer fixture') } 'Size mismatch'
Check-Package missing { param($root) Remove-Item -LiteralPath (Join-Path $root 'tray/EntreePrintTray.exe') } 'Missing package file'
Check-Package extra { param($root) [IO.File]::WriteAllText((Join-Path $root 'stray-token.txt'), 'fixture') } 'Unexpected package file'
Check-Package hidden { param($root) $path = Join-Path $root '.secret'; [IO.File]::WriteAllText($path, 'fixture'); [IO.File]::SetAttributes($path, [IO.FileAttributes]::Hidden) } 'Unexpected package file'
Check-Package incomplete { param($root, $manifest) $manifest.files = @($manifest.files | Where-Object { $_.path -ne 'sdk/native.mjs' }) } 'Required package file is not listed'
Check-Package empty { param($root, $manifest) $manifest.files = @() } 'The package manifest is empty'
Check-Package duplicate { param($root, $manifest) $manifest.files += $manifest.files[0] } 'Duplicate manifest path'
Check-Package duplicateCase { param($root, $manifest) $entry = $manifest.files[0].PSObject.Copy(); $entry.path = $entry.path.ToUpperInvariant(); $manifest.files += $entry } 'Duplicate manifest path'
Check-Package traversal { param($root, $manifest) $manifest.files[0].path = '../outside.txt' } 'Invalid manifest file path'
Check-Package absolute { param($root, $manifest) $manifest.files[0].path = 'C:/outside.txt' } 'Invalid manifest file path'
Check-Package backslash { param($root, $manifest) $manifest.files[0].path = 'service\..\outside.txt' } 'Invalid manifest file path'
Check-Package stream { param($root, $manifest) $manifest.files[0].path = 'sdk/native.mjs:secret' } 'Invalid manifest file path'
Check-Package trailingDot { param($root, $manifest) $manifest.files[0].path = 'sdk/native.mjs.' } 'Invalid manifest file path'
Check-Package invalidHash { param($root, $manifest) $manifest.files[0].sha256 = 'wrong' } 'Invalid manifest digest or size'
Check-Package negativeSize { param($root, $manifest) $manifest.files[0].bytes = -1 } 'Invalid manifest digest or size'
Check-Package fractionalSize { param($root, $manifest) $manifest.files[0].bytes = 7.5 } 'Invalid manifest digest or size'
Check-Package metadata { param($root, $manifest) $manifest.PSObject.Properties.Remove('selfContained') } 'Unsupported or incomplete package metadata'
Check-Package missingRuntime { param($root, $manifest) $manifest.selfContained = $true } 'Self-contained runtime file is missing'
Check-Package link { param($root) New-Item -ItemType Junction -Path (Join-Path $root 'linked') -Target $testRoot | Out-Null } 'Links are not package files'
# Reusing an existing output must fail before dotnet publish can touch it.
$guardRoot = Join-Path $testRoot 'valid'
$caught = $null
try { & (Join-Path $PSScriptRoot 'publish.ps1') -OutputRoot $guardRoot | Out-Null }
catch { $caught = $_.Exception.Message }
if ($caught -notlike 'OutputRoot already exists*') { throw "Publish overwrite guard failed: $caught" }
$script:passed++
Write-Host "PASS publish overwrite guard"
Write-Host "$script:passed package checks passed. Synthetic fixtures retained at $testRoot"
