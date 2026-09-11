# Galbox.Core.Saves — Ren'Py save parser

Reverse-engineered against a real game (`Dreamin'_Her`, Ren'Py 7.4.11.2266 / Python 2.7) and its
real save files. The design report this implements is `_product/design/renpy-save-analysis.md`.

Everything here is **read-only** and **zero-execution**: a `.save` file is untrusted input, so no
pickle is ever run, no class is ever resolved and nothing is ever instantiated.

## Quick start

```csharp
using Galbox.Core.Saves;

var analyzer = new RenpySaveAnalyzer();
RenpySaveAnalysis analysis = analyzer.Analyze(@"D:\GAME\Dreamin'_Her");

Console.WriteLine(analysis.Directories.SaveDirectoryName);   // dreamin_her-1631775296
foreach (var save in analysis.Saves)                         // newest first
{
    Console.WriteLine($"{save.SlotName,-14} {save.CurrentSceneLabel,-20} "
                    + $"{save.Playtime,-10:hh\\:mm\\:ss} {save.LastDialogueText}");
}

var cg = analysis.CgProgress!;
Console.WriteLine($"{cg.UnlockedCount}/{cg.TotalCount} = {cg.Percent:F1}%");   // 6/27 = 22.2%
```

Each step is also usable on its own:

```csharp
var dirs      = RenpySaveLocator.Locate(gameDir);                       // Step 0
var meta      = RenpySaveMetadataReader.Read(savePath);                 // Step 1 (no pickle at all)
var log       = RenpyLogReader.Read(RenpySaveMetadataReader.ReadLogBytes(savePath)!); // Step 2-4
var persistent= RenpyPersistentReader.ReadFile(persistentPath);         // Step 5
var cg        = RenpyPersistentReader.ComputeCgProgress(slots, persistent);
var index     = RenpyScriptIndexBuilder.Build(gameRoot, gameFolder);    // Step 3
using var rpa = RpaArchive.Open(@"D:\GAME\X\game\archive.rpa");
```

## Pieces

| File | Responsibility |
|---|---|
| `RenpySaveLocator.cs` | Step 0 — read `config.save_directory`, probe both save locations |
| `RenpySaveMetadataReader.cs` | Step 1 — ZIP metadata (`json`, `renpy_version`, mtime, screenshot) |
| `RenpyLogReader.cs` | Steps 2-4 — `log` pickle → runtime / `Context.current` / dialogue history |
| `RenpyPersistentReader.cs` | Step 5 — `persistent` zlib+pickle → CG slots, seen scenes, choices |
| `RenpyScriptIndexBuilder.cs` | Step 3 — labels, `from _call_*` map, `gallery.image(...)` slots |
| `RenpySaveAnalyzer.cs` | Orchestrator and the public `Analyze()` entry point |
| `RenpySaveModels.cs` | The DTOs / records callers consume |
| `Pickle/PickleScanner.cs` | The zero-execution pickle byte-stream decoder |
| `Pickle/PickleValue.cs` | The data model the scanner produces |
| `Pickle/PickleScanAudit.cs` | Audit trail: opcode counts, referenced globals, threat report |
| `Rpa/RpaArchive.cs` | RPA-3.0 archive reader (`SubFile` semantics + magic validation) |

## Non-obvious rules this encodes

1. **A `.save` is a ZIP, not a zlib stream.** Five entries; `json` is plain text.
2. **There is no `_save_time`** — Ren'Py uses the file mtime, so we do too.
3. **`Context.current`'s 3-tuple is `(file, compile_timestamp, statement_serial)`.** The third
   element is a global statement serial, *never* a line number.
4. **`persistent._seen_images` keys are tuples.** `renpy.seen_image("0101")` looks up `('0101',)`;
   comparing the raw slot string yields an empty intersection.
5. **CG rate is an intersection**, not `len(_seen_images)` (283 keys vs 6 unlocked slots).
6. **Both save locations are written on every save** — pick by mtime, never hard-code one.
7. **The patch copy of a script is the live one** (`00patch/` before `game/`).
8. **RPA members are `SubFile`s**: the logical payload is `start + file[offset ..]`, and the index
   `start` field is served *before* the on-disk bytes.
9. **Helper labels are not scenes.** Labels defined in `macro` scripts (`face`, `characg`, ...) are
   real labels but not story scenes, so they are excluded from `SceneSequence` and from
   `CurrentSceneLabel` unless nothing else is available.

## Verification

The acceptance harness lives outside the repository (it writes no files into it) and is run with:

```
dotnet run --project E:\tmp\_galbox_parser_scratch\verify -c Debug
```

It reproduces every ground-truth number from the design report and proves the scanner cannot
execute save content. See `E:\tmp\_galbox_parser_scratch\evidence\acceptance.txt` for the raw output.
