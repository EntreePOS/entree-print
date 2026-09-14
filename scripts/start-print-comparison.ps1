param([int]$Port = 19779)
$ErrorActionPreference = 'Stop'
if ($Port -ne 19779) { throw 'This demo uses loopback port 19779.' }
$project = Join-Path $PSScriptRoot '..\samples\PrintComparison\PrintComparison.csproj'
$reviewBuild = Join-Path $env:LOCALAPPDATA 'EntreePrintComparison\build'
dotnet run --project $project --artifacts-path $reviewBuild --no-launch-profile
