# Galbox.Services

Application services that need **both** the engine parsers (`Galbox.Core`) and the database
(`Galbox.Data`). It currently holds one feature: the save-node scan that turns a game's save
directory into `SaveNode` rows.

```
Galbox.Core  ──┐
               ├──►  Galbox.Services  ──►  Galbox.App (UI, wired up separately)
Galbox.Data  ──┘
```

## Why a separate project instead of putting the service in Core or Data

The scan service is the *only* thing in the repository that must reference the parser **and** the
persistence layer, and both obvious alternatives pay for that with something the project cares about:

| Option | Why it was rejected / accepted |
|---|---|
| (a) **new `Galbox.Services`** (chosen) | Keeps the dependency direction one-way and explicit. `Galbox.Core` currently has **zero** persistence dependency (it only carries `Microsoft.Extensions.DependencyInjection` + `System.Text.Json`); giving it `ProjectReference → Galbox.Data` would drag EF Core + SQLite into every consumer of the parser, including the parser's own verification harness. `Galbox.Services` makes the "orchestration + storage" dependency visible in one csproj. |
| (b) inside `Galbox.Core` + `ProjectReference → Galbox.Data` | Smallest diff, but it reverses the layering for good: `Galbox.Core.Saves` is documented as read-only, zero-execution parsing with no UI or persistence dependency, and a future "parse a save file on its own" caller (import, repair, tests) would then have to load EF Core. |
| (c) inside `Galbox.Data` | The data layer is deliberately parser-agnostic ("the data layer never parses saves itself", see `SaveNode.ParseStatus`). Putting the Ren'Py mapping there would have the entity layer know about `RenpySaveSlot`. |
| (d) inside `Galbox.App` | Rejected by the task: another workstream owns `Galbox.App` (DI refactor) and this feature must not touch it. |

`Galbox.Services` references exactly `Galbox.Core` + `Galbox.Data`; nothing references it yet, so
adding it cannot disturb any existing build. When the UI workstream wires the feature up it adds one
`ProjectReference` to `Galbox.App` and one DI registration:

```csharp
// Scope: the DbContext it needs is scoped, so register the service as scoped/transient, not singleton.
services.AddScoped<ISaveNodeScanService, SaveNodeScanService>();
```

## What is in here

| File | Responsibility |
|---|---|
| `Saves/SaveNodeScanService.cs` | Orchestration: analyse → map → upsert, idempotently, with an honest failure report. |
| `Saves/RenpySaveNodeMapper.cs` | Every product judgement (field mapping, parse status, honesty rules) and nothing else. Pure; no DB, no UI. |
| `Saves/SceneSetClusterer.cs` | Deterministic Jaccard/single-linkage clustering of saves by visited scene sets — the only remaining signal for "which branch". |
| `Saves/SaveNodeExtendedMetadata.cs` | The typed JSON payload stored in `SaveNode.ExtendedMetadataJson` (evidence + values without a column). |
| `Saves/SaveNodeScanReport.cs` | The scan report the UI shows: counts, per-slot outcome, failure kind and reason. |

## Product rules encoded here

From `_product/design/save-node-integration-plan.md`:

* **§3.2.1 CG is game-level.** `persistent` is a shared file; the CG numbers are written to every node
  and mean "as of this scan". They are never the progress of that individual save, and the payload
  says so explicitly (`gameLevel.scope`).
* **§3.2.2 mirrors are one node.** Ren'Py writes every save to two directories. The node stores the
  effective copy (newest mtime, which is what the analyzer already picks); the other copy is recorded
  under `mirrors` in the evidence payload. Slot matching also looks at stored mirror paths, so a flip
  of the "newest copy" cannot create a duplicate node.
* **§3.2.3 idempotent.** Expect `added=0, updated=0, skipped=N` on a second scan of an unchanged
  directory. No unique index exists in the schema, so identity is resolved in this service:
  effective path → mirrored path → slot name.
* **§5 red line 1 — "疑似" only.** No route variable is readable in any of the reference saves. A route
  name is written only when scene-set clustering produced a group of two or more saves, and it always
  carries the `疑似路线` prefix (`RenpySaveNodeMapper.SpeculativeRoutePrefix`). Lone saves keep an empty
  `RouteName`. `SaveGroup` rows are deliberately **not** created: naming a branch is a user decision.
* **§5 red line 2 — no line-percentage "chapter progress".** `ChapterProgressPercent` is
  *unlocked scenes / total scenes* (`persistent._seen_ever` ∩ the script label dictionary). It stays
  `null` when the denominator is unknown, because unknown ≠ 0 %. The game defines no chapters
  (`chapterName` is never written, `sceneProgress.gameDefinesChapters` is `false`).
* **§5 red line 3 — failures are visible.** Every slot is persisted, including failures
  (`ParseStatus.Failed` + a Chinese `ParseError` naming what is missing). Nothing is skipped silently;
  a stale error text is cleared when a later scan succeeds.

## Verified against

The reference game `D:\GAME\Dreamin'_Her` (Ren'Py 7.4.11.2266, Python 2.7) and its 12 real saves.
The acceptance harness lives outside this repository (it is not part of `Galbox.sln`) and can be run
with the command in its own README.
