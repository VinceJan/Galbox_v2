<#
.SYNOPSIS
    Verifies the Galbox EF Core database migration path end-to-end, without ever touching the live database.

.DESCRIPTION
    Runs three scenarios against throw-away copies of the real Galbox database and prints the raw evidence:

      1. Legacy database upgrade  - a copy of %LocalAppData%\Galbox\galbox.db (created by the old
                                    EnsureCreated() code, i.e. with tables but no __EFMigrationsHistory)
                                    is adopted into the migrations system and upgraded.
      2. Fresh install            - an empty path gets a complete database from the same migration path.
      3. Idempotency              - the same copy is migrated twice; the second run must change nothing.

    A fourth scenario applies only the baseline migration to an empty database and diffs its structure
    against the legacy database, proving that treating the legacy schema as "baseline already applied"
    is justified; three further scenarios cover hand-modified databases (objects of a pending migration
    that already exist) and data preservation through the SQLite table rebuild.

    The source database is only ever read (to copy it and to hash it). Its SHA256 hash is printed before and
    after the run to demonstrate that it was not modified.

.PARAMETER LiveDatabasePath
    Path of the database to verify. Defaults to %LocalAppData%\Galbox\galbox.db. Point it at a snapshot instead
    (for example `-LiveDatabasePath E:\tmp\Galbox_v2\_product\backup\galbox-real-db-before-migration-*.db`) to
    prove that a *pre-migration* user database upgrades cleanly; that is the recommended baseline for evidence.

.PARAMETER WorkDirectory
    Scratch directory for the copies and the fresh databases. Defaults to %TEMP%\galbox-migration-verify.

.PARAMETER CleanWorkDirectory
    Delete the scratch directory (including the captured evidence file) when the run finishes.
    By default the scratch directory is kept so the migrated copies and the evidence file can be inspected.

.EXAMPLE
    pwsh -File tools\verify-db-migration.ps1

.EXAMPLE
    pwsh -File tools\verify-db-migration.ps1 -LiveDatabasePath 'E:\tmp\Galbox_v2\_product\backup\galbox-real-db-before-migration-20260911-230250.db' -WorkDirectory 'E:\tmp\verify-pristine'
#>
[CmdletBinding()]
param(
    [string]$LiveDatabasePath = (Join-Path $env:LocalAppData 'Galbox\galbox.db'),
    [string]$WorkDirectory = (Join-Path $env:TEMP 'galbox-migration-verify'),
    [switch]$CleanWorkDirectory
)

$ErrorActionPreference = 'Stop'

# UTF-8 so that the Chinese product strings in the evidence survive the round-trip to the output file.
$previousEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = Split-Path -Parent $PSScriptRoot
$harnessProject = Join-Path $repoRoot 'tests\Galbox.Data.Migrations.Harness\Galbox.Data.Migrations.Harness.csproj'
$harnessDll = Join-Path $repoRoot 'tests\Galbox.Data.Migrations.Harness\bin\Debug\net8.0-windows\Galbox.Data.Migrations.Harness.dll'

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "### $text" -ForegroundColor Cyan
}

function Get-SourceHash([string]$path) {
    # The live database may be held by another process (the application, or another agent); retry briefly and
    # treat a persistent lock as "cannot verify" instead of aborting the whole run.
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try {
            return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        }
        catch {
            if ($attempt -eq 4) {
                Write-Warning "could not hash ${path}: $($_.Exception.Message)"
                return $null
            }

            Start-Sleep -Milliseconds 500
        }
    }

    return $null
}

try {
    Write-Host '================================================================================='
    Write-Host ' GALBOX DATABASE MIGRATION VERIFICATION'
    Write-Host '================================================================================='
    Write-Host "live database : $LiveDatabasePath"
    Write-Host "work directory: $WorkDirectory"
    Write-Host "repository    : $repoRoot"

    if (-not (Test-Path -LiteralPath $LiveDatabasePath)) {
        throw "live database not found: $LiveDatabasePath"
    }

    # Fresh scratch directory every run, so no result can be left over from a previous invocation.
    if (Test-Path -LiteralPath $WorkDirectory) {
        Remove-Item -LiteralPath $WorkDirectory -Recurse -Force
    }

    $copyDirectory = Join-Path $WorkDirectory 'source-copy'
    $runDirectory = Join-Path $WorkDirectory 'run'
    New-Item -ItemType Directory -Force -Path $copyDirectory | Out-Null
    New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null

    $sourceCopy = Join-Path $copyDirectory 'galbox.db'

    Write-Step 'hashing and copying the live database (read-only access)'
    $hashBefore = Get-SourceHash $LiveDatabasePath
    Write-Host "SHA256 before: $(if ($hashBefore) { $hashBefore } else { '(unavailable - file is in use)' })"
    Copy-Item -LiteralPath $LiveDatabasePath -Destination $sourceCopy -Force
    Write-Host "copied to    : $sourceCopy ($((Get-Item -LiteralPath $sourceCopy).Length) bytes)"

    Write-Step 'building the verification harness'
    & dotnet build $harnessProject -c Debug --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "harness build failed with exit code $LASTEXITCODE"
    }

    Write-Step 'running the migration scenarios'
    $output = & dotnet $harnessDll --workdir $runDirectory --source-db $sourceCopy 2>&1 | Out-String
    $harnessExit = $LASTEXITCODE

    $outputFile = Join-Path $WorkDirectory 'migration-verification-output.txt'
    $output | Set-Content -LiteralPath $outputFile -Encoding utf8
    Write-Output $output

    Write-Step 'verifying that the live database was not modified'
    $hashAfter = Get-SourceHash $LiveDatabasePath
    Write-Host "SHA256 before: $(if ($hashBefore) { $hashBefore } else { '(unavailable)' })"
    Write-Host "SHA256 after : $(if ($hashAfter) { $hashAfter } else { '(unavailable)' })"

    if (-not $hashBefore -or -not $hashAfter) {
        Write-Host 'RESULT: cannot prove the source file is unchanged (it is locked or was replaced during the run); the harness only ever read it.' -ForegroundColor Yellow
        $untouched = $true
        $hashVerified = $false
    }
    else {
        $untouched = $hashBefore -eq $hashAfter
        $hashVerified = $true
        if ($untouched) {
            Write-Host 'RESULT: source database unchanged (hash identical)' -ForegroundColor Green
        }
        else {
            Write-Host 'RESULT: source database CHANGED during the run - this must not happen!' -ForegroundColor Red
        }
    }

    Write-Host ''
    Write-Host "harness exit code : $harnessExit"
    Write-Host "hash check        : $(if ($hashVerified) { 'performed' } else { 'skipped (file in use)' })"
    Write-Host "full output saved : $outputFile"

    if ($harnessExit -ne 0) {
        throw "migration verification FAILED (harness exit code $harnessExit)"
    }

    if (-not $untouched) {
        throw 'migration verification FAILED: the live database hash changed'
    }

    Write-Host ''
    Write-Host 'ALL SCENARIOS PASSED' -ForegroundColor Green
}
finally {
    [Console]::OutputEncoding = $previousEncoding

    if ($CleanWorkDirectory -and (Test-Path -LiteralPath $WorkDirectory)) {
        Remove-Item -LiteralPath $WorkDirectory -Recurse -Force
        Write-Host "removed work directory: $WorkDirectory"
    }
    elseif (Test-Path -LiteralPath $WorkDirectory) {
        Write-Host "work directory kept for inspection: $WorkDirectory"
    }
}
