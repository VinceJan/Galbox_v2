# Galbox.Acceptance

A headless acceptance runner for Galbox. It is deliberately **not** a unit test project:
it builds the real dependency-injection container (minus the two UI-bound services), drives the
real business services against a **real installed game**, and prints a PASS/FAIL report with the
values it actually measured.

It exists because "it compiles" was being treated as "it works".

## What it does

| Check | What it measures |
|-------|------------------|
| A0 | The acceptance database is isolated from the real one, the EF Core model is created from scratch, a `GameInfo` round-trips through SQLite, and a second reset proves the run is repeatable. Also prints the state of the game library. |
| A1 | `IGameUtilityService.FindExecutableInFolder` picks the intended launcher from a folder that contains both a 64-bit and a 32-bit `.exe`. |
| A2 | `IGameUtilityService.CalculateFolderSize` reports the real folder size (cross-checked against an independent recursive walk, so a silently swallowed `UnauthorizedAccessException` cannot pass). |
| A3 | `ISaveManagementService.DetectSaveLocationAsync` finds the save folder; every field of `SaveLocationResult` is printed. |
| A4 | `IErrorCheckingService.CheckGameAsync` runs to completion, reports well-formed findings, and persists exactly as many `ErrorRecords` rows as findings. An empty finding list is a valid pass. |
| A5 | **Live scraping.** `IGameScrapingService.SearchGameAsync` across the four sources; per source it prints success, error message, raw exception, hit count, elapsed time and the matched titles. |
| A6 | Whether the A5 hits are *usable* metadata: title, rating, cover URL presence per item, plus the per-source totals. |
| A7 | Upstream contract probe. Prints the traffic the application's **own** HttpClient pipelines produced during A5 (recorded by a transparent handler, so no request literals to go stale), then issues a *corrected* request to the same upstreams to prove whether a scraping failure lives in Galbox or upstream. Also dumps each typed `HttpClient`'s `BaseAddress`. |

Exit code: `0` when every check passes, `1` when anything fails or errors.

## How to run

```powershell
# from the repository root
dotnet build Galbox.sln -c Debug
.\tests\Galbox.Acceptance\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Galbox.Acceptance.exe
```

Building the project directly puts the executable somewhere else, which is easy to trip over:

```powershell
dotnet build tests\Galbox.Acceptance\Galbox.Acceptance.csproj -c Debug
.\tests\Galbox.Acceptance\bin\Debug\net8.0-windows10.0.19041.0\win-x64\Galbox.Acceptance.exe
```

Options:

```
--game <folder>      game folder to test against   (default D:\GAME\Dreamin'_Her)
--name <query>       name used for the GameInfo and the scraping query (default Dreamin' Her)
--timeout <seconds>  global timeout for the whole run (default 300)
--verbose, -v        print every captured service log record, not just WARNING and above
```

## What it deliberately does not do

* **No WinUI.** The container registers every service the shipping app registers **except**
  `INavigationService`/`NavigationService`, which are the only ones that require
  `Microsoft.UI.Xaml.Controls`. Nothing that is under test is skipped.
* **No user data.** The database lives at
  `%LocalAppData%\Galbox\acceptance\acceptance.db` and is deleted and recreated on every run.
  The real `%LocalAppData%\Galbox\galbox.db` is never opened.
* **No scraping cache.** The persistent cache is disabled
  (`AcceptanceContainer.DisableScrapingCache`), so A5 is always a genuine network query and
  `%LocalAppData%\Galbox\ScrapingCache` is neither read nor written.
* **No `src/` changes.** The runner may only add files under `tests/`. If a check fails, the
  fix belongs in `src/`, not in this project.

## Adding a check

Implement `IAcceptanceCheck` (see `CheckResult.cs`), add it to the array in `Program.cs`, and
put the measured value in `CheckResult.Actual` — never in the title. A check that cannot state
what it measured is not a check.
