param(
    [Parameter(Mandatory = $true)][string]$PackageRoot,
    [Parameter(Mandatory = $true)][string]$CompilerPath,
    [string]$OutputRoot = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repoRoot 'dist\installer' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw 'OutputRoot already exists. Choose a fresh installer directory.' }
if ($OutputRoot.StartsWith($PackageRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer output must be outside the input package.' }
& (Join-Path $PSScriptRoot 'verify-package.ps1') -PackageRoot $PackageRoot | Out-Host
& (Join-Path $PSScriptRoot 'verify-binary-assets.ps1') -PackageRoot $PackageRoot | Out-Host
$manifest = Get-Content -LiteralPath (Join-Path $PackageRoot 'manifest.sha256.json') -Raw | ConvertFrom-Json
if (-not $manifest.selfContained -or $manifest.configuration -ne 'Release') { throw 'The installer requires a Release self-contained package.' }
$compilerVersion = (& $CompilerPath --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $compilerVersion -ne '7.1.0') { throw 'Use the pinned Inno Setup 7.1.0 compiler.' }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
& $CompilerPath "/DPackageRoot=$PackageRoot" "/DOutputRoot=$OutputRoot" (Join-Path $repoRoot 'installer\EntreePrint.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed; this output directory is incomplete.' }
$installerPath = Join-Path $OutputRoot "EntreePrint-$($manifest.version)-win-x64-setup.exe"
$installerFile = Get-Item -LiteralPath $installerPath
$sha256 = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $OutputRoot 'SHA256SUMS.txt'), "$sha256  $($installerFile.Name)`n", [Text.UTF8Encoding]::new($false))
[pscustomobject]@{
    version = $manifest.version; installer = $installerFile.Name; bytes = $installerFile.Length; sha256 = $sha256
    packageManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $PackageRoot 'manifest.sha256.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    compilerVersion = $compilerVersion; signed = (Get-AuthenticodeSignature -LiteralPath $installerPath).Status -eq 'Valid'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'build-info.json') -Encoding UTF8
Write-Host "Built installer: $installerPath"
Write-Host 'Compilation is not installation verification. Complete the installer and printer pilots before a public release.'
