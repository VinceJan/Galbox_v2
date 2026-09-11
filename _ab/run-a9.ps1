# One A/B measurement of A9.
#   .\_ab\run-a9.ps1 -Label prefix-nonoise -Noise 0
#   .\_ab\run-a9.ps1 -Label prefix-noise   -Noise 1
param(
    [Parameter(Mandatory = $true)][string]$Label,
    [int]$Noise = 0,
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$root = "E:\tmp\Galbox_e2e"
$runDir = Join-Path $root "_ab\run-$Label"
$dataDir = Join-Path $runDir "appdata"
$noiseRoot = Join-Path $runDir "noise"
$noisePidFile = Join-Path $runDir "noise-pids.txt"
$logFile = Join-Path $root "_ab\a9-$Label.log"

Remove-Item -Recurse -Force $runDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $runDir, $dataDir, (Join-Path $dataDir 'ScrapingCache') | Out-Null

# The A9 precondition, planted where the launched application will actually look for it: inside the
# data folder the runner exports. (The pre-fix check planted it in the shared
# %LocalAppData%\Galbox\ScrapingCache, which the child would only read when GALBOX_DATA_DIR was unset.)
$probe = '{"GameName":"A9 ab probe","Result":{"Query":"A9 ab probe","SourceResults":{},"Errors":[]},' +
         '"CachedAt":"2026-01-01T00:00:00Z","ExpiresAt":"2030-01-01T00:00:00Z",' +
         '"BestMatchSource":null,"BestMatchScore":null}'
Set-Content -Path (Join-Path $dataDir 'ScrapingCache\search_a9-ab-probe.json') -Value $probe -Encoding UTF8

$env:GALBOX_ACCEPTANCE_DIR = $runDir
$env:GALBOX_DATA_DIR = $dataDir

$noiseProcess = $null
if ($Noise -eq 1) {
    $noiseProcess = Start-Process -FilePath "pwsh" -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root '_ab\a9-noise.ps1'),
        '-NoiseRoot', $noiseRoot, '-PidFile', $noisePidFile
    )
    Start-Sleep -Seconds 3
}

$exe = Join-Path $root 'tests\Galbox.Acceptance\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Galbox.Acceptance.exe'
Push-Location $root
try {
    & $exe --timeout $TimeoutSeconds 2>&1 | Tee-Object -FilePath $logFile | Out-Null
    $exit = $LASTEXITCODE
} finally {
    Pop-Location
}

if ($noiseProcess) {
    try { Stop-Process -Id $noiseProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
}
if (Test-Path $noisePidFile) {
    foreach ($line in (Get-Content $noisePidFile)) {
        $noisePid = 0
        if ([int]::TryParse($line.Trim(), [ref]$noisePid)) {
            try { Stop-Process -Id $noisePid -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
}

Write-Host "===================== $Label : exit code $exit ====================="
$text = Get-Content $logFile -Raw
$block = [regex]::Match($text, '(?s)# \[A9\].*?RESULT   : \w+.*?(?=\r?\n# \[|\r?\n=+)')
if ($block.Success) { Write-Host $block.Value } else { Write-Host "(A9 block not found)" }
Write-Host "--- summary lines ---"
($text -split "`n") | Where-Object { $_ -match '^ A9 ' -or $_ -match 'EXIT CODE' } | ForEach-Object { Write-Host $_ }
