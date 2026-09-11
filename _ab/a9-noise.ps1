# A9 concurrent-log-writer fixture.
#
# Launches REAL Galbox.App.exe instances, each against its own GALBOX_DATA_DIR that contains a
# DIRECTORY named "galbox.db" (so SQLite cannot open the database). The application therefore takes
# its genuine OnLaunched failure path and writes "EXCEPTION in OnLaunched ..." into its startup log.
#
# Before the A9 isolation fix StartupDiagnostics ignored GALBOX_DATA_DIR and always appended to the
# single shared %LocalAppData%\Galbox\logs\startup-YYYYMMDD.log - the very file the A9 check was
# measuring. This fixture reproduces that "another Galbox.App.exe writes the same log" situation on
# demand, and keeps only a handful of processes alive at a time (each one sits on its failure dialog
# until it is reaped).
param(
    [string]$NoiseRoot = "E:\tmp\Galbox_e2e\_ab\noise",
    [string]$Exe       = "E:\tmp\Galbox_e2e\src\Galbox.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Galbox.App.exe",
    [int]$DurationSeconds = 90,
    [int]$GapMs        = 550,
    [int]$LifetimeSeconds = 4,
    [string]$PidFile   = "E:\tmp\Galbox_e2e\_ab\noise-pids.txt"
)

New-Item -ItemType Directory -Force -Path $NoiseRoot | Out-Null
"" | Set-Content -Path $PidFile -Encoding ASCII

$startInfos = @()
for ($w = 0; $w -lt 3; $w++) {
    $dir = Join-Path $NoiseRoot "worker$w"
    Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $dir 'galbox.db') | Out-Null   # SQLite cannot open this
    New-Item -ItemType Directory -Force -Path (Join-Path $dir 'ScrapingCache') | Out-Null

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.WorkingDirectory = (Split-Path $Exe)
    $psi.UseShellExecute = $false
    $psi.Environment['GALBOX_DATA_DIR'] = $dir
    $startInfos += ,$psi
}

$alive = @()
$deadline = (Get-Date).AddSeconds($DurationSeconds)
$round = 0

while ((Get-Date) -lt $deadline) {
    $psi = $startInfos[$round % $startInfos.Count]
    $round++
    try {
        $p = [System.Diagnostics.Process]::Start($psi)
        if ($p) {
            Add-Content -Path $PidFile -Value $p.Id -Encoding ASCII
            $alive += ,@($p.Id, (Get-Date))
        }
    } catch { }

    $keep = @()
    foreach ($entry in $alive) {
        if (((Get-Date) - $entry[1]).TotalSeconds -gt $LifetimeSeconds) {
            try { Stop-Process -Id $entry[0] -Force -ErrorAction SilentlyContinue } catch { }
        } else {
            $keep += ,$entry
        }
    }
    $alive = $keep

    Start-Sleep -Milliseconds $GapMs
}

foreach ($entry in $alive) {
    try { Stop-Process -Id $entry[0] -Force -ErrorAction SilentlyContinue } catch { }
}
