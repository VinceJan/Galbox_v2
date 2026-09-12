# Evidence: GUI checks must not disturb the developer's desktop

Raw measurements behind `docs/DEVELOPER-GUIDE.md` §6.1. All values are **verbatim tool output**.
Nothing here is inferred; where a number is missing, the text says so.

Machine: two displays, `\\.\DISPLAY2` 3840×2160 @150% (primary) and `\\.\DISPLAY1` 2560×1440 at
physical offset (3840, 208). **All coordinates below are physical pixels** - both instruments call
`SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` before reading anything, because a
DPI-unaware process is handed coordinates already divided by 1.5 on this machine. That difference is
measurable and was hit during this work: the same window at physical `-32000` reads as `-21333`
through a DPI-unaware process. Both are the same place; only one of them is the truth.

The instruments are **independent of the code under test** (they only call Win32 and UI Automation
from outside), so the acceptance suite cannot make them agree with it.

---

## 1. Before: the window lands on the developer's desktop

`release/1.0.0` at `b4c8086`, built separately from an untouched `git archive` of that revision
(`E:\tmp\galbox-pristine`), started the way every GUI check starts it:

```
Launched pid      : 6564

FIRST WINDOW at     : 1713 ms
  class             : WinUIDesktopWin32WindowClass
  title             : "Galbox 1.0.0"
  GetWindowRect     : (left=342, top=342, right=2142, bottom=1542) [1800x1200]
  IsWindowVisible   : True
  window intersects a monitor: True

--- final verdict ---
  MainWindowHandle  : 0xA04A0 (non-zero)
  GetWindowRect     : (left=342, top=342, right=2142, bottom=1542) [1800x1200]
  IsWindowVisible   : True
  window intersects a monitor: True
  OFFSCREEN         : False
  MainWindowTitle   : "Galbox 1.0.0"
```

`(342, 342)` is inside DISPLAY2 `(0, 0, 3840, 2160)`. That is the window the user saw, four times
per acceptance run - and A19/A50 then paged through the product inside it.

## 2. After: same assertions, window off every monitor

`fix/gui-checks-must-not-flash`, same instrument, same polling:

```
### switch UNSET (a normal developer start) ###
  GetWindowRect     : (left=380, top=380, right=2180, bottom=1580) [1800x1200]
  IsWindowVisible   : True
  window intersects a monitor: True
  MainWindowHandle  : 0xB091A (non-zero)
  OFFSCREEN         : False
  MainWindowTitle   : "Galbox 1.0.0"

### GALBOX_TEST_OFFSCREEN_WINDOW=1 (what the suite sets) ###
  GetWindowRect     : (left=-32000, top=-32000, right=-30200, bottom=-30800) [1800x1200]
  IsWindowVisible   : True
  window intersects a monitor: False
  MainWindowHandle  : 0x2099E (non-zero)
  OFFSCREEN         : True
  MainWindowTitle   : "Galbox 1.0.0"
```

Read the three values together, because all three matter:

| | before | normal start (switch unset) | suite start (switch on) |
|---|---|---|---|
| `GetWindowRect` | `(342, 342, 2142, 1542)` | `(380, 380, 2180, 1580)` | `(-32000, -32000, -30200, -30800)` |
| `IsWindowVisible` | True | True | **True** |
| `MainWindowHandle` | non-zero | non-zero | **non-zero** |
| title | `Galbox 1.0.0` | `Galbox 1.0.0` | **`Galbox 1.0.0`** |
| window ∩ monitor | yes | yes | **no** |
| window size | 1800×1200 | 1800×1200 | **1800×1200** |

The size is identical in all three cases, which is the point of moving the window before the
DPI/work-area clamp rather than after it. The switch-off row is unchanged behaviour - that is the
row a user gets.

## 3. The four GUI checks, as the harness itself reported them

Full acceptance run, exit code **0**, `PASSED : 43 / FAILED : 0 / ERRORS : 0 / TOTAL : 43`.
The harness reports its own measurement next to every verdict:

```
# [A9] The GUI application opens a window while scrape-cache data is present
ACTUAL   : window present: MainWindowHandle=0x30228 ("Galbox 1.0.0"), startup log reports
           completion, pid alive after 1799 ms, log isolated to pid 15020 (11 lines, 0 foreign),
           window off-screen at (left=-32000, top=-32000, right=-30200, bottom=-30800)
           with IsWindowVisible=True
  MainWindowHandle     : 0x30228 (non-zero)
  Visible top-level windows owned by the process: 1
  Window placement     : hwnd=0x30228, GetWindowRect=(left=-32000, top=-32000, right=-30200,
                         bottom=-30800) [1800x1200], IsWindowVisible=True,
                         intersects a monitor=False => offscreen=True
RESULT   : PASS  (1919 ms)

# [A18] The application window title is the product name, not "WinUI Desktop"
ACTUAL   : title="Galbox 1.0.0", startsWithGalbox=True, hostDefault=False,
           placement=[hwnd=0x50838, GetWindowRect=(left=-32000, top=-32000, right=-30200,
           bottom=-30800) [1800x1200], IsWindowVisible=True, intersects a monitor=False
           => offscreen=True]
RESULT   : PASS  (1891 ms)

# [A19] Every navigation destination loads inside the running application
ACTUAL   : pagesLoaded=8/8, window=True, alive=True, attempts=1,
           placement=[hwnd=0x3909DC, GetWindowRect=(left=-32000, top=-32000, right=-30200,
           bottom=-30800) [1800x1200], IsWindowVisible=True, intersects a monitor=False
           => offscreen=True]
    pages loaded        : 8/8
    window off-screen   : True (IsWindowVisible=True)
RESULT   : PASS  (2143 ms)

# [A50] Rapid continuous navigation keeps the process alive
ACTUAL   : survived 200 rapid switch(es) over 12.8s, exit code never observed,
           window off-screen at (left=-32000, top=-32000, right=-30200, bottom=-30800)
           with IsWindowVisible=True
  Window placement     : hwnd=0xC083C, GetWindowRect=(left=-32000, top=-32000, right=-30200,
                         bottom=-30800) [1800x1200], IsWindowVisible=True,
                         intersects a monitor=False => offscreen=True
  Navigation items reachable through UI Automation: 6 [主页, 游戏库, 存档管理, 补丁中心, 刮削进度, 错误报告]
  Pacing               : 40 ms between selections, target 200 switches
  Switches completed   : 200 / 200 attempted (target 200, floor 120)
RESULT   : PASS  (14708 ms)
```

A50 is the load-bearing one for "nothing was weakened": **200 switches, 40 ms apart, target and
pacing untouched**, and the window was 200 × (`-32000, -32000`).

## 4. Nothing appeared on the desktop for the whole run

A separate watchdog process sampled every top-level window of every `Galbox.App` process
continuously (5 ms requested, ~13.6 ms achieved - 66095 scans over 900 s, 90729 window samples)
while `dotnet test` **and** the complete acceptance suite ran under it. Final report, verbatim:

```
Scans             : 66095
Window samples of Galbox.App windows: 90729
VIOLATIONS (visible Galbox window overlapping a monitor): 0

RESULT: no Galbox window ever appeared on this desktop during the watch.
```

Exit code 0. Every window that became visible during those 15 minutes did so at
`(-32000, -32000)`; 35 of them did, one per launch (the acceptance suite retries a launch when
another tool kills the process, which is why there are more launches than checks). The per-window
detail the watchdog printed for each of them reads:

```
hwnd=0x3B0A4A  pid=31896  first=(left=152, top=152, right=3032, bottom=1689) [2880x1537]
                           last=(left=-32000, top=-32000, right=-30200, bottom=-30800) [1800x1200]
       first visible at 799214 ms : (left=-32000, top=-32000, right=-30200, bottom=-30800) (on a monitor: False)
```

`first visible at ... on a monitor: False` is the sentence that matters: the window existed at an
interim position before it was shown (the platform sizes it once before `MainWindow` runs), but the
**first moment it was visible** was already off every monitor, so there was nothing to see.

### The watchdog was validated against a known-positive first

An instrument that never reports anything proves nothing, so the same watchdog was pointed at the
**unfixed** build. Raw output:

```
 3653 ms : hwnd=0x209AA BECAME VISIBLE at (left=152, top=152, right=1952, bottom=1352) [1800x1200] (on a monitor: True)
Scans             : 1066
VIOLATIONS (visible Galbox window overlapping a monitor): 465
RESULT: the watched run put a visible Galbox window on the desktop.
```

465 violation samples against the old build, 0 against the new one, same instrument, same machine.

### A 15-minute watch that *did* report violations - and why that is the strongest evidence here

One watchdog run (10:54:13, 62575 scans) reported **1093** violation samples and exited 1. That run
is kept rather than hidden, because every one of those samples belongs to a **deliberate control
launch with the switch unset** - the three baseline measurements in sections 2 and 6 - and the
timeline shows the instrument telling the two cases apart in the same session, minutes apart:

```
=== every window that BECAME VISIBLE during the 15-minute watch ===
10:54:25  hwnd=0x30228   onMonitor=False      <- acceptance run, A9
10:54:32  hwnd=0x50838   onMonitor=False      <- acceptance run, A18
10:54:34  hwnd=0x3909DC  onMonitor=False      <- acceptance run, A19
10:55:02  hwnd=0xC083C   onMonitor=False      <- acceptance run, A50
10:55:50  hwnd=0x80838   onMonitor=True       <- CONTROL, launched by hand with the switch UNSET
10:55:58  hwnd=0x1C082E  onMonitor=False
10:56:28  hwnd=0x3B0976  onMonitor=True       <- CONTROL, "normal start" row of the taskbar table
10:56:36  hwnd=0x3C0976  onMonitor=False
10:57:25  hwnd=0x20A34   onMonitor=False
10:57:49  hwnd=0x50A88   onMonitor=False
10:58:15  hwnd=0x80A7A   onMonitor=False      <- the WS_EX_TOOLWINDOW experiment
10:59:27  hwnd=0x70A70   onMonitor=True       <- CONTROL, "switch unset" row of the four-value check
10:59:36  hwnd=0x50A32   onMonitor=False
11:02:29  hwnd=0xA09EA   onMonitor=False      <- tools/release.ps1 launch probe
11:03:12  hwnd=0x1E001E  onMonitor=False      <- dotnet test
11:04:41  hwnd=0xF09EA   onMonitor=False      <- final acceptance run, A9
11:04:52  hwnd=0x120A34  onMonitor=False      <- final acceptance run, A18
11:05:02  hwnd=0x220810  onMonitor=False
11:05:05  hwnd=0x160A34  onMonitor=False
11:05:27  hwnd=0x120926  onMonitor=False
11:05:29  hwnd=0x1308B4  onMonitor=False
11:05:34  hwnd=0x4609A8  onMonitor=False
11:05:36  hwnd=0x33099A  onMonitor=False      <- final acceptance run, A50
11:06:00  hwnd=0xB0A7E   onMonitor=False
```

Every window opened by a harness - the acceptance suite, `dotnet test`, the release probe - became
visible at `(-32000, -32000)`. The only three that became visible on a monitor are the three times
this investigation started the application **by hand with the switch left unset**, on purpose, to
measure what a normal start does. Put differently: the harness runs never appear in the
`onMonitor=True` rows, and the control launches never fail to.

## 5. The two launch sites outside the acceptance suite

### `tests/Galbox.Tests` (`dotnet test`)

Run with the watchdog up, and with `GALBOX_DATA_DIR` pointed at a throwaway folder so that the
user's real `%LocalAppData%\Galbox\galbox.db` is never opened:

```
dotnet test tests\Galbox.Tests\Galbox.Tests.csproj -c Debug --filter "FullyQualifiedName~AppLaunchSmokeTests"
已通过! - 失败:     0，通过:     1，已跳过:     0，总计:     1

watchdog:  28397 ms : hwnd=0x1E001E BECAME VISIBLE at (left=-32000, top=-32000, ...) (on a monitor: False)
               30 s : scans=2062, distinct Galbox windows=4, violations so far=0
```

Only `AppLaunchSmokeTests` was selected. `Step9_StartupDiagnosticsAndDatabaseAreReal` was **not**
selected on purpose: it asserts on `%LocalAppData%\Galbox\galbox.db` by absolute path, so running it
would have required opening the user's real database. Every other test in that project carries
`[Fact(Skip = ...)]`. `Step9` uses the same launcher (`AppLaunchObserver.LaunchRetryingExternalKills`)
as the test that was run, so the launch path this change touches is covered.

### `tools/release.ps1`

The release probe asserts `MainWindowHandle != 0`, reads the title, and requires a clean exit from
`CloseMainWindow()` - all three are position-independent, so the probe now runs off-screen by default
(`-OnScreen` opts back in). Full run, exit code 0:

```
=== 3. 实机启动验证（窗口必须真的出现） ===
  探针以离屏方式启动（GALBOX_TEST_OFFSCREEN_WINDOW=1），不会打扰正在用这台机器的人；加 -OnScreen 可改回屏幕内
  PID 2968 于 11:02:27.584 启动
  [OK]   MainWindowHandle : 657898
  [OK]   窗口标题         : Galbox 1.0.0
  [OK]   启动耗时         : 2.4 秒
  [OK]   正常关闭（消息循环正常）
```

This ran against the **self-contained publish** (328 files, 171.8 MB, `WindowsAppSDKSelfContained`),
not a Debug build, so `MainWindowHandle` staying non-zero is verified for the artifact users get.
The switch name is read out of `src\Galbox.App\MainWindow.xaml.cs` at run time rather than copied
into the script; a rename fails the release instead of silently re-enabling pop-ups.

Note the ordering limit: the switch only works once the **published executable** supports it. A
package published before this change (e.g. `Galbox-1.0.0-win-x64` from `967ed5e`) ignores the
variable and still shows its window. That is not a bug in the script.

## 6. The taskbar, measured (and a wrong theory corrected)

Off every monitor is not the same as out of sight: the window still had a taskbar button that
appeared and disappeared once per GUI check. Measured with a UI Automation sweep of `Shell_TrayWnd`:

```
### A: normal start (switch unset) ###
TASKBAR BUTTON FOR GALBOX   : PRESENT (1)
    Galbox.App - 1 个运行窗口  [ControlType.Button]

### B: off-screen only (no WS_EX_TOOLWINDOW) ###
TASKBAR BUTTON FOR GALBOX   : PRESENT (1)
    Galbox.App - 1 个运行窗口  [ControlType.Button]
```

The comment in §6.1 used to say `WS_EX_TOOLWINDOW` was avoided because it "risks zeroing
`Process.MainWindowHandle`". That was **speculation and it is wrong**. Set on a live window from
outside the process:

```
=== STEP 2: apply WS_EX_TOOLWINDOW from outside the process ===
exStyle before = 0x00000100  (TOOLWINDOW=False)
exStyle after  = 0x00000180  (TOOLWINDOW=True)

=== STEP 3: AFTER ===
MainWindowHandle = 0x80A7A  (zero? False)
MainWindowTitle  = 'Galbox 1.0.0'
GetWindowRect     : (left=-32000, top=-32000, right=-30200, bottom=-30800)
IsWindowVisible   : True
window intersects a monitor: False
TASKBAR BUTTON FOR GALBOX   : ABSENT
```

All three required values hold, so the bit is now applied by `MainWindow.HideFromTaskbarAndAltTab()`
when - and only when - the off-screen switch is on.

`AppWindow.IsShownInSwitchers = false` (WinAppSDK 1.6.250205002) was tried first and is a **no-op**
for this unpackaged window - the extended style was still `0x00000100` and the taskbar entry was
still there afterwards. That is why the raw Win32 bit is used.

Finally, the assertion A50 depends on - resolving the window through UI Automation's root - still
works with the tool-window bit set:

```
CASE 1: switch unset      -> UIA ROOT LOOKUP : FOUND after 12 ms (name 'Galbox 1.0.0')
CASE 2: switch on         -> UIA ROOT LOOKUP : FOUND after  5 ms (name 'Galbox 1.0.0')
```

## 7. Reproducing this

Inside the acceptance suite, nothing extra is needed: A9/A18/A19/A50 print their own
`Window placement :` line, and it must read `intersects a monitor=False` with
`IsWindowVisible=True`. That line is the check's own evidence, and a window on a monitor fails the
check.

From outside, any process that enumerates windows will do. The measurements above came from two
throwaway instruments kept outside the repository for the duration of this change
(`E:\tmp\galbox-offscreen-probe`, a console app plus three PowerShell scripts); they are not part of
the build. The essential technique is small enough to re-create:

```powershell
# where is a running Galbox window, and is it on any display?
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace Win -Name Api -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@
$monitors = [System.Windows.Forms.Screen]::AllScreens | ForEach-Object { $_.Bounds }
foreach ($p in (Get-Process Galbox.App -ErrorAction SilentlyContinue)) {
  $h = $p.MainWindowHandle; if ($h -eq 0) { continue }
  $r = [Win.Api+RECT]::new(); [void][Win.Api]::GetWindowRect($h, [ref]$r)
  $onMonitor = $false
  foreach ($m in $monitors) {
    if ($r.Left -lt $m.Right -and $m.Left -lt $r.Right -and $r.Top -lt $m.Bottom -and $m.Top -lt $r.Bottom) { $onMonitor = $true }
  }
  "hwnd=0x$('{0:X}' -f $h) rect=(left=$($r.Left), top=$($r.Top), right=$($r.Right), bottom=$($r.Bottom)) IsWindowVisible=$([Win.Api]::IsWindowVisible($h)) intersects a monitor=$onMonitor"
}
```
