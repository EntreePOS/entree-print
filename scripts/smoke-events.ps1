param(
    [int]$HttpPort = 19179,
    [int]$DiscoveryPort = 19178,
    [string]$Configuration = "Debug",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$serviceProject = Join-Path $repoRoot "csharp\EntreePrintPlugin\EntreePrintPlugin.csproj"
$serviceDll = Join-Path $repoRoot "csharp\EntreePrintPlugin\bin\$Configuration\net9.0-windows\EntreePrintPlugin.dll"

if (-not $SkipBuild) {
    dotnet build $serviceProject -c $Configuration | Out-Host
}

if (-not (Test-Path $serviceDll)) {
    throw "Service DLL not found: $serviceDll"
}

$serviceJob = Start-Job -ScriptBlock {
    param($workingDirectory, $dllPath, $httpPortValue, $discoveryPortValue)

    Set-Location $workingDirectory
    Set-Item Env:HTTP_PORT ([string]$httpPortValue)
    Set-Item Env:DISCOVERY_PORT ([string]$discoveryPortValue)
    Set-Item Env:HTML_PRINT_MODE "render"
    Set-Item Env:PRINTER_STATUS_REFRESH_SECONDS "60"
    Set-Item Env:PRINT_RETRY_DELAY_MS "50"

    & dotnet $dllPath
} -ArgumentList $repoRoot.Path, $serviceDll, $HttpPort, $DiscoveryPort

$eventJob = $null

try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $healthRaw = & curl.exe -s "http://127.0.0.1:$HttpPort/health"
        if ($LASTEXITCODE -eq 0 -and $healthRaw -match '"ok":true') {
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 250
    }

    if (-not $ready) {
        throw "Service did not become healthy on port $HttpPort."
    }

    $eventJob = Start-Job -ScriptBlock {
        param($port)

        $request = [System.Net.HttpWebRequest]::Create("http://127.0.0.1:$port/events")
        $request.Timeout = 10000
        $request.ReadWriteTimeout = 10000
        $response = $request.GetResponse()
        try {
            $reader = [System.IO.StreamReader]::new($response.GetResponseStream())
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
            while ([DateTimeOffset]::UtcNow -lt $deadline) {
                $line = $reader.ReadLine()
                if ($line -match '^event: (printer|printers|job)$') {
                    return $line
                }
            }
        }
        finally {
            $response.Dispose()
        }
    } -ArgumentList $HttpPort

    Start-Sleep -Milliseconds 500
    & curl.exe -s "http://127.0.0.1:$HttpPort/printers?refresh=true" | Out-Null
    $eventLine = Wait-Job $eventJob -Timeout 12 | Receive-Job

    if (-not $eventLine) {
        throw "No live event was received from /events."
    }

    [pscustomobject]@{
        ok = $true
        event = [string]$eventLine
    } | ConvertTo-Json -Compress
}
finally {
    if ($eventJob) {
        Stop-Job $eventJob -ErrorAction SilentlyContinue | Out-Null
        Remove-Job $eventJob -Force -ErrorAction SilentlyContinue
    }
    Stop-Job $serviceJob -ErrorAction SilentlyContinue | Out-Null
    Remove-Job $serviceJob -Force -ErrorAction SilentlyContinue
}
