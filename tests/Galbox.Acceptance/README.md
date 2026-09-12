# Galbox.Acceptance

A headless acceptance runner for Galbox. It is deliberately **not** a unit test project:
it builds the real dependency-injection container (minus the two UI-bound services), drives the
real business services against a **real installed game**, and prints a PASS/FAIL report with the
values it actually measured.

It exists because "it compiles" was being treated as "it works".

## What it does

> The table below explains only the checks that were documented when it was written
> (A0–A10, A40–A42). The runner registers **43** checks in total; the authoritative list — ids,
> titles and execution order — is the array in `Program.cs`, and a readable inventory is in the
> repository README. Update this table when you add a check.

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
| A8 | Navigation / page / XAML completeness — the regression guard for "implemented but no door": every navigation key resolves to an existing page type, every ViewModel registered in `App.xaml.cs` is referenced by a view under `Views/`, every page is reachable, every converter a view uses is registered, and every `MainWindow.xaml` menu `Tag` is a known key. Source-level on purpose: a runtime test can never see a missing XAML file. |
| A9 | The check that starts the shipping GUI against a private data folder. With at least one `search_*.json` file present, `Galbox.App.exe` must stay alive and own a visible top-level window within 30 s (`MainWindowHandle` cross-checked with an `EnumWindows` scan) **and** write a startup log that reports completion — a window alone is not accepted, because the startup-failure dialog is a window too. It exists because the worst defect so far — a thread-affinity `COMException` that left a live process with no window — was invisible to every headless check. The window is required to be off every monitor (the run may not disturb the desktop); that is measured, not assumed. |
| A10 | **The save-node feature, end to end.** Resolves `ISaveNodeScanService` from the app-shaped container (the wiring assertion), scans a real game and requires exactly 12 de-duplicated nodes with non-empty scene labels, `auto-3` → 孤独感, CG 6/27 with the id set `{0101,0301,0401,0501,0801,2401}`, a second scan that inserts nothing, a missing/empty directory that fails with a concrete reason and leaves no row behind, and — driven through the real `SaveManagerViewModel` — a timeline with no blank label, 自动档/手动档 separation, the 疑似 route marker, the honest progress figure, the visible parse-failure state, the "还差 21 张" list, the empty state and the unsupported-engine state. |
| A40 | **ymgal live search.** Resolves the real `YmgalApi` from the container, searches a real title (`サノバウィッチ`), and asserts the client config, that a request actually went to `/open/archive/search-game`, that the payload binds into the model, and that `GetGameAsync` returns that archive. |
| A41 | **cngal live search.** Same shape for `CngalApi` with `三色绘恋`, asserting the documented `/api/home/Search` path and the entry-detail lookup. |
| A42 | **Metadata-source behaviour contract** (no network assumptions required): both clients have a BaseAddress + identifying User-Agent + bounded timeout; an incomplete ymgal credential fails fast, names the missing environment variable and issues **0** requests; an unreachable cngal endpoint is reported as a transport failure, not as an empty result; and a legitimate zero-hit answer is reported as `Success = true` with no `LastError`. |

Exit code: `0` when every check passes, `1` when anything fails or errors.

## GUI checks must not disturb the developer's desktop (hard rule)

Four checks start the real `Galbox.App.exe`: **A9** (process alive + a visible top-level window +
the startup log reports completion), **A18** (the real window's caption), **A19** (all eight
navigation destinations loaded inside the running application) and **A50** (200 rapid navigation
switches). Two of them make the window page through the product by itself.

The suite is run over and over while several work lines build in parallel, on the machine somebody
is actually using. So every one of those checks starts the application with
`GALBOX_TEST_OFFSCREEN_WINDOW=1` (`OffscreenWindow.Request`), which makes
`Galbox.App.MainWindow` place its window at `(-32000, -32000)` before it is ever shown.

**Nothing is weakened by this.** Win32 does not care where a window is: an off-screen window still
reports `IsWindowVisible = TRUE`, still has a non-zero `Process.MainWindowHandle`, still has its
title, still sits under the UI Automation root, and still loads and switches pages. Every original
condition is untouched; each check adds one measured requirement on top - the window rectangle must
not intersect any monitor (`OffscreenWindow.Measure` returns the real `GetWindowRect`,
`IsWindowVisible` and the monitor intersection). A window that lands on a monitor fails the check
with `OffscreenWindow.WouldBeVisibleReason` instead of quietly flashing at whoever is at the desk.

When adding a check that starts the real application: call `OffscreenWindow.Request(startInfo)` and
put `placement.IsVisibleAndOffscreen` into the verdict. Do not start a GUI check any other way.
See `docs/DEVELOPER-GUIDE.md` §6.1 for the before/after measurements and the residual risk (the
window is off every monitor, but it may still get a taskbar button - removing that would risk
`MainWindowHandle` going to zero, which is exactly what A9 exists to assert).

## Metadata source configuration (ymgal / cngal)

Neither source requires the user to apply for a key:

* **ymgal** is OAuth2 client-credentials. Its developer documentation publishes a shared public
  client, so the client works out of the box. A dedicated client id (recommended for a distributed
  app) can be supplied through the environment and takes precedence:
  `GALBOX_YMGAL_CLIENT_ID`, `GALBOX_YMGAL_CLIENT_SECRET` (set **both**, or neither),
  `GALBOX_YMGAL_BASE_URL` (host override).
* **cngal** is a fully open API with no authentication at all. Only `GALBOX_CNGAL_BASE_URL`
  exists, for mirrors.

A half-filled credential pair is reported as a configuration failure naming the missing variable -
it never silently falls back to the public client, and it never issues a request.

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
* **No user data.** Each run gets its own database under a per-run folder,
  `%LocalAppData%\Galbox\acceptance\run-<pid>\acceptance.db` (deleted and recreated per run).
  The real `%LocalAppData%\Galbox\galbox.db` is never opened.
  Set `GALBOX_ACCEPTANCE_DIR` to an absolute folder to move it: otherwise the parent
  `acceptance` folder is shared by every worktree on the machine, and two harnesses running at once
  interfere with each other (observed: A0 failing with `IOException: the file is being used by
  another process`, a run that seeded a screenshot picking up another worktree's game row, and —
  because every harness starts and closes a `Galbox.App.exe` with the same image name — one
  harness's GUI checks losing the window they were measuring).
  Running two harnesses at once also means two windows appearing on the desktop; see the warning in
  the repository README.
* **No scraping cache.** The persistent cache is disabled
  (`AcceptanceContainer.DisableScrapingCache`), so A5 is always a genuine network query and
  `%LocalAppData%\Galbox\ScrapingCache` is neither read nor written.
* **No `src/` changes.** The runner may only add files under `tests/`. If a check fails, the
  fix belongs in `src/`, not in this project.

## Adding a check

Implement `IAcceptanceCheck` (see `CheckResult.cs`), add it to the array in `Program.cs`, and
put the measured value in `CheckResult.Actual` — never in the title. A check that cannot state
what it measured is not a check. If it starts the real application, it must also follow the hard
rule above: off-screen, and measured to be off-screen.
