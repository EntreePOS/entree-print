param([Parameter(Mandatory = $true)][string]$PackageRoot)
$ErrorActionPreference = 'Stop'
$rootItem = Get-Item -LiteralPath $PackageRoot -Force
if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'PackageRoot must be a regular directory.'
}
$PackageRoot = $rootItem.FullName.TrimEnd('\', '/')
# Reject links before following any paths from the manifest.
$pending = New-Object 'System.Collections.Generic.Queue[string]'
$pending.Enqueue($PackageRoot)
$actualFiles = New-Object 'System.Collections.Generic.List[object]'
while ($pending.Count -gt 0) {
    foreach ($item in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Links are not package files: $($item.Name)" }
        if ($item.PSIsContainer) { $pending.Enqueue($item.FullName) }
        else { $actualFiles.Add($item) }
    }
}
$manifestPath = Join-Path $PackageRoot 'manifest.sha256.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -cne 'entree-print-package-0.0.1' -or $manifest.algorithm -cne 'sha256' -or
    $manifest.version -cne '0.0.1-beta' -or $manifest.runtime -cne 'win-x64' -or
    $manifest.configuration -cnotin @('Debug', 'Release') -or $manifest.selfContained -isnot [bool]) {
    throw 'Unsupported or incomplete package metadata.'
}
if ($null -eq $manifest.files -or @($manifest.files).Count -eq 0) { throw 'The package manifest is empty.' }
$expectedByPath = @{}
foreach ($entry in $manifest.files) {
    $relative = $entry.path
    if ($relative -isnot [string] -or $relative -notmatch '^[a-zA-Z0-9_.-]+(?:/[a-zA-Z0-9_.-]+)*$' -or
        @($relative.Split('/') | Where-Object { $_ -eq '.' -or $_ -eq '..' -or $_.EndsWith('.') }).Count -gt 0 -or
        $relative -ieq 'manifest.sha256.json') { throw 'Invalid manifest file path.' }
    if ($expectedByPath.ContainsKey($relative)) { throw "Duplicate manifest path: $relative" }
    if ($entry.sha256 -isnot [string] -or $entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
        ($entry.bytes -isnot [long] -and $entry.bytes -isnot [int]) -or $entry.bytes -lt 0) {
        throw "Invalid manifest digest or size: $relative"
    }
    $expectedByPath[$relative] = $entry
}
foreach ($required in @(
    'service/EntreePrintPlugin.exe', 'service/EntreePrintPlugin.dll', 'service/EntreePrintPlugin.runtimeconfig.json', 'service/appsettings.json',
    'tray/EntreePrintTray.exe', 'tray/EntreePrintTray.dll', 'tray/EntreePrintTray.runtimeconfig.json',
    'sdk/entree-print.mjs', 'sdk/outbox.mjs', 'sdk/native.mjs', 'sdk/native-discovery.mjs', 'sdk/sqlite-outbox.mjs', 'sdk/package.json', 'sdk/README.md',
    'scripts/verify-package.ps1', 'scripts/uninstall-service.ps1',
    'README.md', 'BETA_SETUP.md', 'API_DESIGN.md', 'API_V2_IMPLEMENTATION.md', 'BETA_RELEASE_CHECKLIST.md'
)) {
    if (-not $expectedByPath.ContainsKey($required)) { throw "Required package file is not listed: $required" }
}
foreach ($item in $actualFiles) {
    $relative = $item.FullName.Substring($PackageRoot.Length + 1).Replace('\', '/')
    if ($relative -ieq 'manifest.sha256.json') { continue }
    if (-not $expectedByPath.ContainsKey($relative)) { throw "Unexpected package file: $relative" }
}
if ($manifest.selfContained) {
    foreach ($folder in @('service', 'tray')) {
        foreach ($runtimeFile in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'clrjit.dll', 'System.Private.CoreLib.dll')) {
            if (-not $expectedByPath.ContainsKey("$folder/$runtimeFile")) { throw "Self-contained runtime file is missing: $folder/$runtimeFile" }
        }
    }
}
foreach ($entry in $manifest.files) {
    $fullPath = Join-Path $PackageRoot $entry.path.Replace('/', '\')
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "Missing package file: $($entry.path)" }
    $item = Get-Item -LiteralPath $fullPath
    if ($item.Length -ne $entry.bytes) { throw "Size mismatch: $($entry.path)" }
    if ((Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash -ine $entry.sha256) { throw "Hash mismatch: $($entry.path)" }
}
[pscustomobject]@{ ok = $true; packageRoot = $PackageRoot; version = $manifest.version; files = @($manifest.files).Count } | ConvertTo-Json -Compress
