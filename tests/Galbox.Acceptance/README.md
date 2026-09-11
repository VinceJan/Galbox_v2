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
| A8 | Source-level completeness: every navigation key maps to a page that exists, every DI-registered ViewModel is referenced by a view, every page is reachable, every converter a view uses is registered, and every MainWindow menu item maps to a navigation key. Guards the defect class "implemented but no door". |
| A9 | Starts the shipping `Galbox.App.exe` with a populated scrape cache and requires a live process that owns a visible top-level window **and** a startup log that reports completion (a window alone is not accepted — the startup-failure dialog is a window too). |
| A10 | **The save-node feature, end to end.** Resolves `ISaveNodeScanService` from the app-shaped container (the wiring assertion), scans a real game and requires exactly 12 de-duplicated nodes with non-empty scene labels, `auto-3` → 孤独感, CG 6/27 with the id set `{0101,0301,0401,0501,0801,2401}`, a second scan that inserts nothing, a missing/empty directory that fails with a concrete reason and leaves no row behind, and — driven through the real `SaveManagerViewModel` — a timeline with no blank label, 自动档/手动档 separation, the 疑似 route marker, the honest progress figure, the visible parse-failure state, the "还差 21 张" list, the empty state and the unsupported-engine state. |

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
  Set `GALBOX_ACCEPTANCE_DIR` to an absolute folder to move it: the default path is shared by every
  worktree on the machine, and two harnesses running at once corrupt each other (observed: A0
  failing with `IOException: the file is being used by another process`, and a run that seeded a
  screenshot picking up another worktree's game row).
* **No scraping cache.** The persistent cache is disabled
  (`AcceptanceContainer.DisableScrapingCache`), so A5 is always a genuine network query and
  `%LocalAppData%\Galbox\ScrapingCache` is neither read nor written.
* **No `src/` changes.** The runner may only add files under `tests/`. If a check fails, the
  fix belongs in `src/`, not in this project.

## Adding a check

Implement `IAcceptanceCheck` (see `CheckResult.cs`), add it to the array in `Program.cs`, and
put the measured value in `CheckResult.Actual` — never in the title. A check that cannot state
what it measured is not a check.
