param(
    [int]$HttpPort = 18979,
    [int]$DiscoveryPort = 18978,
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

    & dotnet $dllPath
} -ArgumentList $repoRoot.Path, $serviceDll, $HttpPort, $DiscoveryPort

$headersPath = Join-Path $env:TEMP ("entree-api-headers-" + [guid]::NewGuid().ToString("N") + ".txt")
$bodyPath = Join-Path $env:TEMP ("entree-api-body-" + [guid]::NewGuid().ToString("N") + ".js")

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

    & curl.exe -s -D $headersPath -o $bodyPath "http://127.0.0.1:$HttpPort/api.js"
    if ($LASTEXITCODE -ne 0) {
        throw "curl api.js failed."
    }

    $headersText = Get-Content -LiteralPath $headersPath -Raw
    $apiHeader = [regex]::Match($headersText, '(?im)^X-Entree-SDK-SHA256:\s*([a-f0-9]{64})\s*$').Groups[1].Value
    $apiVersion = [regex]::Match($headersText, '(?im)^X-Entree-SDK-Version:\s*(\S+)\s*$').Groups[1].Value
    $apiHash = (Get-FileHash -LiteralPath $bodyPath -Algorithm SHA256).Hash.ToLowerInvariant()

    $socket = [System.Net.WebSockets.ClientWebSocket]::new()
    try {
        $socket.ConnectAsync([Uri]::new("ws://127.0.0.1:$HttpPort/sdk"), [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $buffer = [byte[]]::new(65536)
        $result = $socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $socketPayload = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count) | ConvertFrom-Json
    }
    finally {
        $socket.Dispose()
    }

    $socketSdkBytes = [System.Text.Encoding]::UTF8.GetBytes($socketPayload.sdk)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $socketHash = [System.BitConverter]::ToString($sha256.ComputeHash($socketSdkBytes)).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }

    $udp = [System.Net.Sockets.UdpClient]::new()
    try {
        $udp.Client.ReceiveTimeout = 3000
        $bytes = [System.Text.Encoding]::UTF8.GetBytes("ENTREE_PRINT_DISCOVER")
        [void]$udp.Send($bytes, $bytes.Length, "127.0.0.1", $DiscoveryPort)
        $remote = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
        $udpPayload = [System.Text.Encoding]::UTF8.GetString($udp.Receive([ref]$remote)) | ConvertFrom-Json
    }
    finally {
        $udp.Dispose()
    }

    $summary = [pscustomobject]@{
        apiHeaderMatchesBody = $apiHeader -eq $apiHash
        apiVersionHeader = $apiVersion
        socketHasChecksum = $socketPayload.sdkChecksum -match "^[a-f0-9]{64}$"
        socketChecksumMatchesSdk = $socketPayload.sdkChecksum -eq $socketHash
        udpHasChecksum = $udpPayload.sdkChecksum -match "^[a-f0-9]{64}$"
        udpSdkVersion = $udpPayload.sdkVersion
        udpAlgorithm = $udpPayload.checksumAlgorithm
    }

    $summary | ConvertTo-Json -Compress

    if (-not $summary.apiHeaderMatchesBody -or
        -not $summary.socketHasChecksum -or
        -not $summary.socketChecksumMatchesSdk -or
        -not $summary.udpHasChecksum) {
        throw "SDK integrity smoke check failed."
    }
}
finally {
    Remove-Item -LiteralPath $headersPath, $bodyPath -Force -ErrorAction SilentlyContinue
    Stop-Job $serviceJob -ErrorAction SilentlyContinue | Out-Null
    Remove-Job $serviceJob -Force -ErrorAction SilentlyContinue
}
