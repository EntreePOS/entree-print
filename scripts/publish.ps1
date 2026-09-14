param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [string]$OutputRoot = '',
    [string]$ArtifactsPath = '',
    [switch]$SelfContained
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
$version = [string]$buildProperties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$') { throw 'Invalid build version.' }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "dist\EntreePrint-$version-$Runtime"
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
# Never publish over an installed service or blend stale files into a new release.
if (Test-Path -LiteralPath $OutputRoot) { throw 'OutputRoot already exists. Select a new package directory.' }
if ([string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    $ArtifactsPath = Join-Path ([IO.Path]::GetTempPath()) ('EntreePublishBuild-' + [Guid]::NewGuid().ToString('N'))
}
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
if ($ArtifactsPath.Equals($OutputRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $ArtifactsPath.StartsWith($OutputRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Build artifacts must be outside the package directory.'
}
$sdk = Get-Content -LiteralPath (Join-Path $repoRoot 'sdk\package.json') -Raw | ConvertFrom-Json
if ($sdk.version -ne $version) { throw 'SDK and application versions do not match.' }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
foreach ($project in @(
    @{ Name = 'EntreePrintPlugin'; Folder = 'service' },
    @{ Name = 'EntreePrintTray'; Folder = 'tray' }
)) {
    $projectPath = Join-Path $repoRoot "$($project.Name)\$($project.Name).csproj"
    dotnet publish $projectPath -c $Configuration -r $Runtime --self-contained $SelfContained.IsPresent.ToString().ToLowerInvariant() --artifacts-path $ArtifactsPath -o (Join-Path $OutputRoot $project.Folder)
    if ($LASTEXITCODE -ne 0) { throw "$($project.Name) publish failed. Incomplete output is not a package; use a new directory after fixing the build." }
}
New-Item -ItemType Directory -Path (Join-Path $OutputRoot 'sdk'), (Join-Path $OutputRoot 'scripts') | Out-Null
foreach ($name in @('entree-print.mjs', 'outbox.mjs', 'events.mjs', 'native.mjs', 'native-discovery.mjs', 'sqlite-outbox.mjs', 'package.json', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "sdk\$name") -Destination (Join-Path $OutputRoot "sdk\$name")
}
foreach ($name in @('verify-package.ps1', 'uninstall-service.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $OutputRoot "scripts\$name")
}
foreach ($name in @('README.md', 'BETA_SETUP.md', 'API_DESIGN.md', 'API_V2_IMPLEMENTATION.md', 'BETA_RELEASE_CHECKLIST.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination (Join-Path $OutputRoot $name)
}
$files = @(Get-ChildItem -LiteralPath $OutputRoot -Recurse -Force -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{
        path = $_.FullName.Substring($OutputRoot.Length + 1).Replace('\', '/')
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = $_.Length
    }
})
[pscustomobject]@{
    schema = 'entree-print-package-0.0.1'
    version = $version
    algorithm = 'sha256'
    configuration = $Configuration
    runtime = $Runtime
    selfContained = $SelfContained.IsPresent
    files = $files
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputRoot 'manifest.sha256.json') -Encoding UTF8
& (Join-Path $PSScriptRoot 'verify-package.ps1') -PackageRoot $OutputRoot
& (Join-Path $PSScriptRoot 'verify-binary-assets.ps1') -PackageRoot $OutputRoot
Write-Host "Created $version package: $OutputRoot"
Write-Host 'Package integrity passed. Deployment and printer pilot gates still apply; see BETA_RELEASE_CHECKLIST.md.'
