param(
    [int]$Jobs = 12,
    [int]$WarmupJobs = 1,
    [int]$HttpPort = 18779,
    [int]$DiscoveryPort = 18778,
    [string]$Printer = "Microsoft Print to PDF",
    [string]$Configuration = "Release",
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
    Set-Item Env:CORS_ALLOWED_ORIGINS "*"
    Set-Item Env:ASPNETCORE_ENVIRONMENT "Production"
    Set-Item Env:Logging__LogLevel__Default "Warning"
    Set-Item Env:Logging__LogLevel__Microsoft.AspNetCore "Warning"

    & dotnet $dllPath
} -ArgumentList $repoRoot.Path, $serviceDll, $HttpPort, $DiscoveryPort

$nodeScript = $null

try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$HttpPort/health" -TimeoutSec 2
            if ($health.ok -eq $true) {
                $ready = $true
                break
            }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }

    if (-not $ready) {
        Write-Host "Service output:"
        Receive-Job $serviceJob -Keep | Select-Object -Last 80 | Out-Host
        throw "Service did not become healthy on port $HttpPort."
    }

    $nodeScript = Join-Path $env:TEMP ("entree-print-render-benchmark-" + [guid]::NewGuid().ToString("N") + ".js")
    Set-Content -LiteralPath $nodeScript -Encoding UTF8 -Value @'
const crypto = require("crypto");
const { performance } = require("perf_hooks");

const port = Number(process.env.BENCH_HTTP_PORT);
const jobs = Number(process.env.BENCH_JOBS);
const warmupJobs = Number(process.env.BENCH_WARMUP_JOBS);
const printer = process.env.BENCH_PRINTER || "";
const base = `http://127.0.0.1:${port}`;
const terminalStatuses = new Set(["completed", "failed", "rendered"]);

function stableStringify(value) {
  if (value === null) return "null";
  if (Array.isArray(value)) return `[${value.map(stableStringify).join(",")}]`;
  if (typeof value === "object") {
    return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${stableStringify(value[key])}`).join(",")}}`;
  }
  return JSON.stringify(value);
}

function checksum(command) {
  const copy = { ...command };
  delete copy.checksum;
  return crypto.createHash("sha256").update(stableStringify(copy)).digest("hex");
}

async function request(path, options) {
  const response = await fetch(base + path, options);
  const text = await response.text();
  let body = null;
  try {
    body = text ? JSON.parse(text) : null;
  } catch {
    body = text;
  }
  if (!response.ok) {
    throw new Error(`${response.status} ${path} ${text}`);
  }
  return body;
}

async function postPrint(index, phase) {
  const id = `perf-${phase}-${Date.now()}-${index}`;
  const command = {
    id,
    printer,
    token: "",
    style: "body{font-family:Arial;margin:0;padding:0}.ticket{width:72mm;padding:8px}.row{display:flex;justify-content:space-between;border-bottom:1px dotted #999;padding:3px 0}.total{font-size:22px;font-weight:700;margin-top:8px}",
    content: [
      { type: "html", html: `<div class=\"ticket\"><h2>ENTREE POS PERF ${index}</h2><div>Order ${1000 + index}</div>` },
      { type: "text", text: "Burger Combo      12.99\nFries              3.50\nTea                2.25" },
      { type: "barcode", format: "code128", value: `PERF${String(index).padStart(4, "0")}`, height: 44, showText: true },
      { type: "html", html: "<div class=\"row\"><span>Subtotal</span><span>$18.74</span></div><div class=\"row\"><span>Tax</span><span>$1.64</span></div><div class=\"total\">TOTAL $20.38</div></div>" }
    ]
  };
  command.checksum = checksum(command);

  const start = performance.now();
  const ack = await request("/print", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(command)
  });
  const ackedAt = performance.now();

  let jobResponse = await request(`/jobs/${encodeURIComponent(ack.id)}`);
  let job = jobResponse.job;
  while (!terminalStatuses.has(job.status)) {
    await new Promise((resolve) => setTimeout(resolve, 50));
    jobResponse = await request(`/jobs/${encodeURIComponent(ack.id)}`);
    job = jobResponse.job;
  }

  const completedAt = performance.now();
  if (job.status === "failed") {
    throw new Error(`Job ${ack.id} failed: ${job.error || job.detail || "unknown error"}`);
  }

  return {
    id,
    ackMs: ackedAt - start,
    totalMs: completedAt - start,
    renderAfterAckMs: completedAt - ackedAt,
    status: job.status,
    artifactPath: job.artifactPath
  };
}

function stats(values) {
  const sorted = [...values].sort((a, b) => a - b);
  const percentile = (p) => sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * p) - 1)];
  return {
    min: sorted[0],
    avg: sorted.reduce((sum, value) => sum + value, 0) / sorted.length,
    p50: percentile(0.5),
    p95: percentile(0.95),
    max: sorted[sorted.length - 1]
  };
}

function roundStats(value) {
  return Object.fromEntries(Object.entries(value).map(([key, number]) => [key, Math.round(number)]));
}

(async () => {
  const warmup = [];
  for (let index = 1; index <= warmupJobs; index++) {
    warmup.push(await postPrint(index, "warmup"));
  }

  const samples = [];
  for (let index = 1; index <= jobs; index++) {
    samples.push(await postPrint(index, "seq"));
  }

  const refreshStartedAt = performance.now();
  const printersResponse = await request("/printers?refresh=true");
  const statusRefreshMs = performance.now() - refreshStartedAt;

  const duplicateCommand = {
    id: samples[0].id,
    printer,
    token: "",
    html: "<body>duplicate check</body>"
  };
  duplicateCommand.checksum = checksum(duplicateCommand);
  const duplicateStartedAt = performance.now();
  const duplicateAck = await request("/print", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(duplicateCommand)
  });
  const duplicateAckMs = performance.now() - duplicateStartedAt;

  const totalMs = samples.reduce((sum, sample) => sum + sample.totalMs, 0);
  const summary = {
    mode: "HTML_PRINT_MODE=render",
    sampleCount: samples.length,
    warmupMs: warmup.map((sample) => ({ ack: Math.round(sample.ackMs), total: Math.round(sample.totalMs) })),
    ackMs: roundStats(stats(samples.map((sample) => sample.ackMs))),
    renderCompletionMs: roundStats(stats(samples.map((sample) => sample.totalMs))),
    renderOnlyAfterAckMs: roundStats(stats(samples.map((sample) => sample.renderAfterAckMs))),
    throughputSequentialJobsPerMinute: Number((60000 / (totalMs / samples.length)).toFixed(1)),
    statusRefreshMs: Math.round(statusRefreshMs),
    printersReturned: printersResponse.printers?.length ?? null,
    duplicateAckMs: Math.round(duplicateAckMs),
    duplicateAckStatus: duplicateAck.status,
    sampleArtifacts: samples.slice(0, 2).map((sample) => sample.artifactPath)
  };

  console.log(JSON.stringify(summary, null, 2));
})().catch((error) => {
  console.error(error.stack || error.message);
  process.exit(1);
});
'@

    Set-Item Env:BENCH_HTTP_PORT ([string]$HttpPort)
    Set-Item Env:BENCH_JOBS ([string]$Jobs)
    Set-Item Env:BENCH_WARMUP_JOBS ([string]$WarmupJobs)
    Set-Item Env:BENCH_PRINTER $Printer

    node $nodeScript
}
finally {
    if ($nodeScript -and (Test-Path $nodeScript)) {
        Remove-Item -LiteralPath $nodeScript -Force
    }
    Stop-Job $serviceJob -ErrorAction SilentlyContinue | Out-Null
    Remove-Job $serviceJob -Force -ErrorAction SilentlyContinue
}
