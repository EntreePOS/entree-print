# Run the lifecycle helper in child shells with all service/firewall calls replaced.
# No administrator rights, live service operations or printer requests are used.
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('EntreeInstallerTests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\installer\service-lifecycle.ps1'))
$nativeDelete = '& "$env:SystemRoot\System32\sc.exe" delete EntreePrintPlugin'
if ($source.Split(@($nativeDelete), [StringSplitOptions]::None).Count -ne 2) { throw 'Review the native service boundary before running this fixture.' }
$stubs = @'
$script:scenario = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'case.json') -Raw -Encoding UTF8 | ConvertFrom-Json
function Get-CimInstance { param($ClassName, $Filter, $ErrorAction) if ($script:scenario.exists) { [pscustomobject]@{PathName=$script:scenario.command;State=$script:scenario.state} } }
function Get-Service {
    param($Name, $ErrorAction)
    $controller = [pscustomobject]@{ Status=$script:scenario.state }
    $controller | Add-Member ScriptMethod WaitForStatus { param($state,$timeout) if ($script:scenario.failure -eq 'wait') { throw 'wait failed' } }
    $controller | Add-Member ScriptMethod Dispose { }
    return $controller
}
function Stop-Service { param($Name,$ErrorAction) if ($script:scenario.failure -eq 'stop') { throw 'stop failed' }; [Console]::WriteLine('STOP') }
function Get-NetFirewallRule { param($ErrorAction) if ($script:scenario.failure -eq 'firewall') { throw 'firewall failed' }; [pscustomobject]@{DisplayName='ENTREE Print Plugin HTTP'}; [pscustomobject]@{DisplayName='Unrelated App'} }
function Remove-NetFirewallRule { param([Parameter(ValueFromPipeline=$true)]$InputObject) process { [Console]::WriteLine('REMOVE RULE: ' + $InputObject.DisplayName) } }
function Invoke-TestServiceDelete { [Console]::WriteLine('DELETE'); $global:LASTEXITCODE = if ($script:scenario.failure -eq 'delete') { 5 } else { 0 } }
function Remove-OwnedTrayStartup { param($Root) [Console]::WriteLine('STARTUP CLEANUP') }
'@
$fixture = $source.Replace($nativeDelete, 'Invoke-TestServiceDelete').Replace("`$ErrorActionPreference = 'Stop'", "`$ErrorActionPreference = 'Stop'`n" + $stubs)
$passed = 0
foreach ($case in @(
    @{name='fresh'; action='Check'; exists=$false; state='Stopped'; error=''},
    @{name='stopped'; action='Check'; exists=$true; state='Stopped'; error=''},
    @{name='running'; action='Check'; exists=$true; state='Running'; error='Stop the Entree Print'},
    @{name='foreign'; action='Uninstall'; exists=$true; state='Running'; foreign=$true; error='Another Entree Print'},
    @{name='uninstall'; action='Uninstall'; exists=$true; state='Running'; error=''},
    @{name='stop-failure'; action='Uninstall'; exists=$true; state='Running'; failure='stop'; error='stop failed'},
    @{name='wait-failure'; action='Uninstall'; exists=$true; state='Running'; failure='wait'; error='wait failed'},
    @{name='firewall-failure'; action='Uninstall'; exists=$true; state='Stopped'; failure='firewall'; error='firewall failed'},
    @{name='delete-failure'; action='Uninstall'; exists=$true; state='Stopped'; failure='delete'; error='Windows could not remove'}
)) {
    $directory = Join-Path $root $case.name
    New-Item -ItemType Directory -Path $directory | Out-Null
    $installRoot = Join-Path $directory '收据 ''$(throw) app'
    New-Item -ItemType Directory -Path $installRoot | Out-Null
    $receipt = Join-Path $installRoot 'saved-receipt.json'
    [IO.File]::WriteAllText($receipt, 'retained-receipt')
    $commandRoot = if ($case.foreign) { Join-Path $directory 'other-install' } else { $installRoot }
    $case.command = '"' + (Join-Path $commandRoot 'service\EntreePrintPlugin.exe') + '"'
    $case | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'case.json') -Encoding UTF8
    $scriptPath = Join-Path $directory 'fixture.ps1'
    [IO.File]::WriteAllText($scriptPath, $fixture, [Text.UTF8Encoding]::new($true))
    $errorFile = Join-Path $directory 'error.txt'
    $start = [Diagnostics.ProcessStartInfo]::new('powershell.exe')
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($arg in @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$scriptPath,'-Action',$case.action,'-InstallRoot',$installRoot,'-ErrorFile',$errorFile)) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'Lifecycle fixture timed out.' }
    $output = $stdout.GetAwaiter().GetResult()
    $errorText = if (Test-Path -LiteralPath $errorFile) { [IO.File]::ReadAllText($errorFile) } else { '' }
    if ($stderr.GetAwaiter().GetResult()) { throw 'Unexpected child-shell error.' }
    if ($case.error) {
        if ($process.ExitCode -ne 1 -or -not $errorText.Contains($case.error)) { throw "$($case.name) failed: $errorText" }
    } elseif ($process.ExitCode -ne 0) { throw "$($case.name) failed: $errorText" }
    if ($case.name -in @('fresh','stopped','running','foreign','stop-failure','wait-failure','firewall-failure') -and $output.Contains('DELETE')) { throw 'Service deleted before prerequisites passed.' }
    if ($case.name -eq 'uninstall' -and (-not $output.Contains('DELETE') -or -not $output.Contains('STOP'))) { throw 'Owned service lifecycle did not finish.' }
    if ($output.Contains('REMOVE RULE: Unrelated App')) { throw 'Unrelated firewall rule was removed.' }
    if ([IO.File]::ReadAllText($receipt) -ne 'retained-receipt') { throw 'Receipt data changed.' }
    $process.Dispose(); $passed++; Write-Host "PASS $($case.name)"
}
Write-Host "$passed installer lifecycle checks passed. Synthetic fixtures: $root"
