# Rapid navigation crash — root cause, fix and raw evidence

Work tree: `E:\tmp\Galbox_crash`, branch `fix/rapid-navigation-crash` (based on `release/1.0.0` @ `25e34ec`).

Symptom under investigation: clicking the navigation menu items back to back kills the whole
process with `0xC000027B` (STATUS_STOWED_EXCEPTION), faulting module `Microsoft.UI.Xaml.dll`.
The crash is **not** related to packaging or deployment: it reproduced in both
self-contained and framework-dependent builds, and it needs *rapid* switching to appear.

## 1. Root cause

`src/Galbox.App/Views/PatchCenterPage.xaml:68` declared a compiled binding whose target control
lives inside a `<Flyout>` that is itself declared in `Page.Resources`:

```xml
<Flyout x:Key="PatchDetailsFlyoutKey" ...>
    <TextBlock Text="{x:Bind ViewModel.PatchDetailContent, Mode=OneWay}" .../>
</Flyout>
```

An `{x:Bind}` inside a resource dictionary is not resolved against the page's normal visual tree.
The XAML compiler folds it into the same generated bindings object
(`PatchCenterPage_obj1_Bindings`) that drives every other binding on the page, and that object's
`Initialize()`/`Update()` walks **all** binding steps — including this one.

WinUI answers `FrameworkElement.Loading` with `Initialize() -> Update()` over the page's *staged*
element tree, i.e. **before** the lazily created flyout content has been attached. At that moment
`Connect()` has not stored the `TextBlock`, so the generated field is still `null` and
`XamlBindingSetters.Set_Microsoft_UI_Xaml_Controls_TextBlock_Text` dereferences it:

```csharp
obj.Text = value ?? global::System.String.Empty;   // obj == null
```

The `NullReferenceException` escapes a XAML callback, WinUI converts it to a stowed exception and
fail-fasts the process. Navigation speed only decides *which* page is live when the broken
callback lands, which is why the crash looked like a navigation problem and only appeared under
rapid switching.

### Direct evidence (not inference)

A temporary probe was injected into the generated binding file
(`obj/.../Views/PatchCenterPage.g.cs`) to print the binding target, the phase, the thread and the
managed call stack at the exact moment of the failure:

```
00:28:33.024 target=NULL valueIsNull=yes phase=-2147483648 tid=1
    PatchCenterPage_obj1_Bindings.Update_ViewModel_PatchDetailContent
    PatchCenterPage_obj1_Bindings.Update_ViewModel
    PatchCenterPage_obj1_Bindings.Update_
    PatchCenterPage_obj1_Bindings.Update
    PatchCenterPage_obj1_Bindings.Initialize
    PatchCenterPage_obj1_Bindings.Loading          <-- FrameworkElement.Loading
    WinRT._EventSource_...TypedEventHandler...EventState.<GetEventInvoke>b__1_0
    ABI.Microsoft.UI.Xaml.IApplicationStaticsMethods.Start
    Microsoft.UI.Xaml.Application.Start
    Galbox.App.Program.Main
```

`target=NULL`, `phase=NOT_PHASED`, `tid=1` (UI thread) — the element was never connected when the
binding was first walked.

### Crash evidence

`firstchance.log` contained exactly one managed exception for the failing run:

```
00:14:46.908 System.NullReferenceException: Object reference not set to an instance of an object.
   at Galbox.App.Views.PatchCenterPage.XamlBindingSetters.Set_Microsoft_UI_Xaml_Controls_TextBlock_Text(TextBlock obj, String value, String targetNullValue)
      in ...\Views\PatchCenterPage.g.cs:line 27
```

Windows event log:

```
出错模块名称： Microsoft.UI.Xaml.dll， 版本： 3.1.6.0
异常代码： 0xc000027b
错误偏移： 0x00000000003a3385
```

WER `APPCRASH` bucket `1c8962f5f94d5e7346526ed78639fe28`, `combase.dll`, exception `80004003`
(E_POINTER — the null dereference observed from the native side).

## 2. Second, previously masked defect

Fixing the flyout binding removed the `NullReferenceException` from `firstchance.log` (verified
A/B), but the process **still** died — with a different signature. The flyout crash had been
faster, so this one never got a chance to run:

```
System.Runtime.InteropServices.COMException
   at ABI.Microsoft.UI.Xaml.Data.PropertyChangedEventArgsRuntimeClassFactory.CreateInstance(String name)
   at ABI.System.ComponentModel.PropertyChangedEventHandler.NativeDelegateWrapper.Invoke(...)
   at CommunityToolkit.Mvvm.ComponentModel.ObservableObject.OnPropertyChanged(...)
   at Galbox.App.ViewModels.PatchCenterViewModel.set_ErrorMessage(String value)
   at Galbox.App.ViewModels.PatchCenterViewModel.LoadPatchesForGameSafeAsync(Int32 gameId)
```

`PatchCenterViewModel.OnSelectedGameChanged` starts the work with `Task.Run`, and
`LoadPatchesForGameSafeAsync` asked for the dispatcher with
`DispatcherQueue.GetForCurrentThread()` — which returns the **calling** thread's queue and is
`null` on a thread-pool thread. The code therefore took its "no dispatcher available" fallback and
touched UI-bound state (`ObservableCollection` clear/add and `PropertyChanged`) from a pool
thread. Creating the WinRT `PropertyChangedEventArgs` needs the thread to have joined an
apartment; the pool thread has not, so it throws and the process dies.

Fix: capture the UI `DispatcherQueue` once in the constructor (resolved on the UI thread) and use
it from both background paths. `ScrapingProgressViewModel` already followed this convention.

## 3. Files changed

| File | Change |
|---|---|
| `src/Galbox.App/Views/PatchCenterPage.xaml` | flyout `TextBlock` gets `x:Name="PatchDetailText"` instead of the `{x:Bind}` |
| `src/Galbox.App/Views/PatchCenterPage.xaml.cs` | `ShowPatchDetailsFlyout` fills the text before showing |
| `src/Galbox.App/ViewModels/PatchCenterViewModel.cs` | UI `DispatcherQueue` captured once, used by both background paths |
| `tests/Galbox.Acceptance/Checks/A50RapidNavigationSurvivalCheck.cs` | new regression check (A50) |
| `tests/Galbox.Acceptance/Program.cs` | A50 appended to the check array (no reordering) |
| `tests/Galbox.Acceptance/Galbox.Acceptance.csproj` | `FrameworkReference` to `Microsoft.WindowsDesktop.App` for UI Automation |

## 4. Probe

`_probe/Nav-Rapid.ps1` is the frozen instrument used before and after the fix. Its intensity was
never reduced: 200 switches, 40 ms pacing, no retry, no backoff, no adaptive slowdown.

```
pwsh -NoProfile -ExecutionPolicy Bypass -File .\_probe\Nav-Rapid.ps1 -Switches 200 -StepMs 40
```

| | Baseline (unfixed) | After the fix |
|---|---|---|
| switches completed | 42 / 200, then 54 / 200, then 55 / 200 (3 runs) | **200 / 200, then 200 / 200** |
| exit code | `0xC000027B` | never exited |
| verdict | `PROBE VERDICT: CRASHED` | `PROBE VERDICT: SURVIVED` |
| elapsed | 12–22 s | 71.2 s / 404 s (the 404 s run was under heavy CPU contention) |

Raw reports: `_probe/artifacts/navrapid-*.txt`.

`_probe/Nav-Rapid-Cdb.ps1` is the same stress with a live `cdb` attached, used while chasing the
native stack.

## 5. Acceptance

`A50` has two halves, both validated as real tests by running them against unfixed code:

* **A50.1 (source guard)** — fails on unfixed code and names the exact line:
  `src\Galbox.App\Views\PatchCenterPage.xaml:68`.
* **A50.2 (runtime)** — with A50.1 temporarily bypassed, it drove the unfixed app and reported
  `the process died after 5 switch(es), exit 0xC000027B = STATUS_STOWED_EXCEPTION`.

With the fix in place A50 passes: 200 rapid switches over 13.4 s, process alive.

`_probe/artifacts/` holds the raw acceptance outputs:
`acceptance-UNFIXED.txt`, `acceptance-FIX1ONLY.txt`, `acceptance-A502-validate.txt`,
`acceptance-FINAL.txt`.

## 6. Known issue NOT fixed here: A9 is broken independently of this work

`A9StartupWithCacheCheck.ReadNewLogLines` compares a **character** count against a **byte** count:

```csharp
var logLengthBefore = new FileInfo(logPath).Length;        // bytes
...
var text = File.ReadAllText(logPath);                      // characters
if (text.Length <= lengthBefore) { return lines; }         // chars vs bytes
```

Measured on this machine: the shared day-file holds 825,810 bytes but 823,311 characters (the
difference is the multibyte UTF-8 Chinese text). One application launch appends ~731 bytes, so
after a launch `text.Length` is still far below `logLengthBefore` and A9 always sees **zero**
lines — it can only ever "see" lines when other Galbox instances write several kilobytes into the
same shared file during its measurement window, and then it reads *their* lines, including their
startup failures.

That is exactly what was observed: with seven concurrent `Galbox_e2e` instances running, A9
reported a failure whose stack trace pointed at `E:\tmp\Galbox_e2e\...`; on a quiet machine it
reports `completed=False, failed=False` and fails unconditionally.

This file was **not modified** — it is byte-identical to `release/1.0.0` and the defect predates
this work. The one-line correction is to compare like with like (read the previous length as
bytes, or decode the appended region from a byte offset). Reported rather than changed because
this task is a targeted navigation-crash fix.
