$ErrorActionPreference = 'Continue'
$root = "E:\tmp\Galbox_e2e"
Set-Location $root

Write-Host "########## RUN 2: PRE-FIX + NOISE ##########"
& pwsh -NoProfile -ExecutionPolicy Bypass -File "$root\_ab\run-a9.ps1" -Label prefix-noise2 -Noise 1 2>&1 |
    Select-String -Pattern 'A9 |EXIT CODE|RESULT|FAIL REASON|Startup log says|EXPECTED|ACTUAL' | ForEach-Object { $_.Line }

Write-Host "########## RESTORE THE FIX ##########"
Copy-Item "$root\_ab_backup\A9StartupWithCacheCheck.cs" "$root\tests\Galbox.Acceptance\Checks\A9StartupWithCacheCheck.cs" -Force
Copy-Item "$root\_ab_backup\StartupDiagnostics.cs" "$root\src\Galbox.App\Services\StartupDiagnostics.cs" -Force

Write-Host "########## BUILD ##########"
dotnet build Galbox.sln -c Debug 2>&1 | Select-Object -Last 5

Write-Host "########## RUN 3: POST-FIX + NOISE ##########"
& pwsh -NoProfile -ExecutionPolicy Bypass -File "$root\_ab\run-a9.ps1" -Label postfix-noise1 -Noise 1 2>&1 |
    Select-String -Pattern 'A9 |EXIT CODE|RESULT|FAIL REASON|Startup log says|EXPECTED|ACTUAL|isolated' | ForEach-Object { $_.Line }

Write-Host "########## RUN 4: POST-FIX + NOISE (repeat) ##########"
& pwsh -NoProfile -ExecutionPolicy Bypass -File "$root\_ab\run-a9.ps1" -Label postfix-noise2 -Noise 1 2>&1 |
    Select-String -Pattern 'A9 |EXIT CODE|RESULT|FAIL REASON|Startup log says|EXPECTED|ACTUAL|isolated' | ForEach-Object { $_.Line }

Write-Host "########## DONE ##########"
