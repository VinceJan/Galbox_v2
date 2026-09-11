using System.Text.Json;
using Galbox.Acceptance.Support;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A92 - the reservation does not fake availability, and it really is only a reservation.
///
/// A91 proves the user cannot reach the two P2 features. A92 proves the other half of the same
/// promise, which is the half a reservation usually gets wrong:
///
///   A92.1 <b>Nothing implements the provider interfaces.</b> If a type implementing
///         <c>IFlowchartProvider</c>, <c>IAchievementProvider</c> or <c>IAchievementEvidenceSource</c>
///         existed, "reserved" would be a polite word for "half-built". The assertion is written so
///         that the day a real implementation lands it fails loudly and points at itself, instead of
///         quietly passing because a name changed.
///   A92.2 <b>Nothing is registered.</b> The app-shaped container resolves null for all three, so
///         even a hypothetical caller would get nothing to call - there is no stub in the graph.
///   A92.3 <b>There are no placeholder bodies.</b> The reserved source files contain no
///         <c>NotImplementedException</c>, no TODO, no FIXME, and no <c>HttpClient</c>: a reservation
///         that ships an empty method body and a "coming soon" comment is exactly what this check
///         refuses.
///   A92.4 <b>The one piece that must work, works.</b> The arrow from the existing save-node data to
///         the reserved flowchart sync is real code with real behaviour: a stored <c>SaveNode</c> is
///         projected onto a story-position sample with every documented value carried across, and a
///         set of nodes is projected in story order. This is measured on a synthesized entity, so it
///         does not touch the user's database.
///   A92.5 <b>The schema is untouched.</b> No EF entity of the model is named after either feature -
///         the reservation must not force a database migration.
/// </summary>
public sealed class A92ReservedNoFakeAvailabilityCheck : IAcceptanceCheck
{
    /// <summary>Vocabulary no reserved source file may carry in a placeholder form.</summary>
    private static readonly string[] PlaceholderTokens = { "NotImplementedException", "TODO", "FIXME" };

    /// <summary>Vocabulary that would mean the reserved layer had started talking to a server.</summary>
    private static readonly string[] NetworkTokens = { "HttpClient", "System.Net.Http", "HttpRequestMessage" };

    /// <summary>The three contracts nothing may implement yet.</summary>
    private static readonly string[] ProviderInterfaceNames =
    {
        "Galbox.Core.Community.Flowcharts.IFlowchartProvider",
        "Galbox.Core.Community.Achievements.IAchievementProvider",
        "Galbox.Core.Community.Achievements.IAchievementEvidenceSource"
    };

    /// <inheritdoc />
    public string Id => "A92";

    /// <inheritdoc />
    public string Title => "reserved features ship no implementation, no stub and no schema change";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "no type in Galbox.Core/Data/Services/App implements IFlowchartProvider, IAchievementProvider or "
            + "IAchievementEvidenceSource; the container resolves null for all three; the reserved source tree "
            + "contains no NotImplementedException / TODO / FIXME and no HttpClient; the SaveNode -> story "
            + "position projection carries every documented value and preserves story order; and the EF model "
            + "gains no entity for either feature";

        var details = new List<string>();
        var failures = new List<string>();
        const string id = "A92";

        var repoRoot = RepoLocator.FindRepoRoot();

        // ------------------------------------------------------------ A92.1 no implementation
        details.Add("--- A92.1 nothing implements the three reserved provider interfaces ---");

        var interfaceTypes = new List<Type>();
        foreach (var name in ProviderInterfaceNames)
        {
            var type = ReservedInterfaceProbe.Find(name);
            if (type is null)
            {
                failures.Add($"{id}: interface '{name}' does not exist, so there is no reservation to check.");
                details.Add($"  [MISSING] {name}");
                continue;
            }

            interfaceTypes.Add(type);
            details.Add($"  [OK]      {name}");

            var implementations = ReservedInterfaceProbe.FindImplementations(type);
            details.Add($"            implementations in {string.Join("/", ReservedInterfaceProbe.ShippingAssemblies)}: "
                      + (implementations.Count == 0 ? "none" : string.Join(", ", implementations.Select(t => t.FullName))));

            if (implementations.Count > 0)
            {
                failures.Add($"{id}: {string.Join(", ", implementations.Select(t => t.FullName))} implements {type.Name}. "
                           + "The first version reserves the interface and ships no implementation; if a real one has "
                           + "now landed, update this check together with it and record why.");
            }
        }

        // ---------------------------------------------------------------- A92.2 no registration
        details.Add(string.Empty);
        details.Add("--- A92.2 the container resolves nothing for the three contracts ---");

        foreach (var type in interfaceTypes)
        {
            object? resolved = null;
            Exception? thrown = null;
            try
            {
                resolved = context.Services.GetService(type);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            var ok = resolved is null && thrown is null;
            details.Add($"  {(ok ? "[OK]   " : "[FAIL] ")} {type.Name,-28} -> {resolved?.GetType().FullName ?? "(null)"}"
                      + (thrown is null ? string.Empty : $"  threw {thrown.GetType().Name}: {thrown.Message}"));

            if (!ok)
            {
                failures.Add($"{id}: the container produced an instance for {type.Name}; a registered stub is a "
                           + "fake availability, not a reservation.");
            }
        }

        details.Add("  (the only registration the reserved layer adds is IReservedFeatureCatalog, which only "
                  + "reports that the features are unavailable - A91 asserts what it says)");

        // ------------------------------------------------------- A92.3 no placeholder bodies
        details.Add(string.Empty);
        details.Add("--- A92.3 reserved source tree: no placeholder body, no half-built HTTP client ---");

        var reservedFolders = new[]
        {
            Path.Combine("src", "Galbox.Core", "Community"),
            Path.Combine("src", "Galbox.Services", "Community"),
            Path.Combine("src", "Galbox.App", "Community")
        };

        if (repoRoot is null)
        {
            failures.Add($"{id}: repository root not found, so the reserved source tree cannot be inspected.");
            details.Add("  [FAIL] repository root not found");
        }
        else
        {
            var reservedSourceFiles = new List<string>();
            foreach (var relative in reservedFolders)
            {
                var folder = Path.Combine(repoRoot, relative);
                if (!Directory.Exists(folder))
                {
                    details.Add($"  [INFO] {relative} does not exist");
                    continue;
                }

                reservedSourceFiles.AddRange(Directory
                    .EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
                    .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                             && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)));
            }

            details.Add($"  files in the reserved source tree: {reservedSourceFiles.Count}");
            foreach (var file in reservedSourceFiles)
            {
                details.Add($"      {Path.GetRelativePath(repoRoot, file)}");
            }

            if (reservedSourceFiles.Count == 0)
            {
                failures.Add($"{id}: the reserved source tree holds no file at all; there is nothing reserved.");
            }

            foreach (var file in reservedSourceFiles)
            {
                var text = File.ReadAllText(file);
                var relative = Path.GetRelativePath(repoRoot, file);

                foreach (var token in PlaceholderTokens)
                {
                    if (text.Contains(token, StringComparison.Ordinal))
                    {
                        failures.Add($"{id}: {relative} contains '{token}'. The reservation must not ship an empty "
                                   + "body or a note-to-self; the interface and its documentation are the deliverable.");
                        details.Add($"  [FAIL] {relative} contains '{token}'");
                    }
                }

                foreach (var token in NetworkTokens)
                {
                    if (text.Contains(token, StringComparison.Ordinal))
                    {
                        failures.Add($"{id}: {relative} mentions '{token}'. Nothing in the reserved layer may talk to "
                                   + "a server; there is no server, and a client that cannot work must not be written.");
                        details.Add($"  [FAIL] {relative} mentions '{token}'");
                    }
                }
            }

            if (reservedSourceFiles.Count > 0)
            {
                details.Add($"  [OK]   0 of {reservedSourceFiles.Count} file(s) contain "
                          + $"{{{string.Join(", ", PlaceholderTokens.Concat(NetworkTokens))}}}");
            }
        }

        // -------------------------------------------------- A92.4 the save-node -> sample arrow
        details.Add(string.Empty);
        details.Add("--- A92.4 SaveNode -> StoryPositionSample: the one arrow that must really work ---");

        var sampleType = ReservedInterfaceProbe.Find($"{ReservedInterfaceProbe.Flowcharts}.StoryPositionSample");
        var projectorType = ReservedInterfaceProbe.Find(
            $"{ReservedInterfaceProbe.ServicesCommunity}.SaveNodePositionProjector",
            ReservedInterfaceProbe.ServicesAssembly);

        if (sampleType is null || projectorType is null)
        {
            details.Add("  [FAIL] StoryPositionSample and/or SaveNodePositionProjector is missing - the documented "
                      + "seam between the stored save nodes and the reserved flowchart sync does not exist.");
            failures.Add($"{id}: the SaveNode -> StoryPositionSample projection cannot be measured because "
                       + $"{(sampleType is null ? "StoryPositionSample" : "SaveNodePositionProjector")} is missing.");
        }
        else
        {
            var projectOne = projectorType.GetMethod("ProjectOne", new[] { typeof(SaveNode) });
            var project = projectorType.GetMethod("Project", new[] { typeof(IEnumerable<SaveNode>) });

            // A synthesized entity, never a database row: this check must not read or write the
            // user's library.
            var node = new SaveNode
            {
                Id = 4242,
                GameInfoId = 77,
                SlotName = "auto-3-LT1",
                SceneLabel = "孤独感",
                RouteName = "kakoroute",
                ChapterName = "第二章 魔女的夜宴",
                ChapterProgressPercent = 42,
                PlayTimeSeconds = 7265,
                SaveModifiedTime = new DateTime(2026, 4, 14, 10, 14, 0, DateTimeKind.Utc),
                CgUnlockedIdsJson = JsonSerializer.Serialize(new[] { "0101", "0301", "2401" }),
                ParseStatus = SaveParseStatus.Parsed
            };

            details.Add($"  probe node (in memory, never persisted): Id={node.Id} GameInfoId={node.GameInfoId} "
                      + $"SceneLabel='{node.SceneLabel}' SlotName='{node.SlotName}'");

            if (projectOne is null)
            {
                failures.Add($"{id}: SaveNodePositionProjector.ProjectOne(SaveNode) does not exist.");
                details.Add("  [FAIL] ProjectOne(SaveNode) not found");
            }
            else
            {
                object? sample = null;
                Exception? projectionFailure = null;
                try
                {
                    sample = projectOne.Invoke(null, new object[] { node });
                }
                catch (Exception ex)
                {
                    projectionFailure = ex;
                }

                if (projectionFailure is not null)
                {
                    var actualException = projectionFailure is System.Reflection.TargetInvocationException { InnerException: { } inner }
                        ? inner
                        : projectionFailure;
                    failures.Add($"{id}: ProjectOne threw {actualException.GetType().Name}: {actualException.Message}");
                    details.Add($"  [FAIL] ProjectOne threw {actualException.GetType().Name}: {actualException.Message}");
                }
                else if (sample is null)
                {
                    failures.Add($"{id}: ProjectOne returned null for a non-null node.");
                    details.Add("  [FAIL] ProjectOne returned null");
                }
                else
                {
                    var carried = new (string Property, object? Expected)[]
                    {
                        ("GameInfoId", node.GameInfoId),
                        ("SaveNodeId", node.Id),
                        ("SlotName", node.SlotName),
                        ("SceneLabel", node.SceneLabel),
                        ("RouteName", node.RouteName),
                        ("ChapterName", node.ChapterName),
                        ("ChapterProgressPercent", node.ChapterProgressPercent),
                        ("PlayTimeSeconds", node.PlayTimeSeconds),
                        ("SavedAtUtc", node.SaveModifiedTime)
                    };

                    foreach (var (property, expectedValue) in carried)
                    {
                        var propertyInfo = sampleType.GetProperty(property);
                        if (propertyInfo is null)
                        {
                            failures.Add($"{id}: StoryPositionSample has no property '{property}'.");
                            details.Add($"  [FAIL] missing property {property}");
                            continue;
                        }

                        var actualValue = propertyInfo.GetValue(sample);
                        var equal = Equals(actualValue, expectedValue);
                        details.Add($"  {(equal ? "[OK]   " : "[FAIL] ")} {property,-24} expected={Format(expectedValue)} "
                                  + $"actual={Format(actualValue)}");

                        if (!equal)
                        {
                            failures.Add($"{id}: the projection lost '{property}' (expected {Format(expectedValue)}, "
                                       + $"measured {Format(actualValue)}); a flowchart sync fed by this sample would "
                                       + "place the save at the wrong node.");
                        }
                    }

                    // The CG list has to survive the JSON column: it is the "已解锁 CG 列表" the
                    // achievement report is required to carry (spec §6.2).
                    var cgProperty = sampleType.GetProperty("CgUnlockedIds");
                    var cgIds = (cgProperty?.GetValue(sample) as IEnumerable<string>)?.ToList() ?? new List<string>();
                    details.Add($"  {(cgIds.SequenceEqual(new[] { "0101", "0301", "2401" }) ? "[OK]   " : "[FAIL] ")} "
                              + $"CgUnlockedIds            expected={{0101,0301,2401}} actual={{{string.Join(",", cgIds)}}}");

                    if (!cgIds.SequenceEqual(new[] { "0101", "0301", "2401" }))
                    {
                        failures.Add($"{id}: the projection did not carry the unlocked CG ids "
                                   + $"(measured {{{string.Join(",", cgIds)}}}).");
                    }

                    // A malformed escape hatch must degrade to "no ids known", never to an exception:
                    // the entity column is free-form JSON written by an engine parser.
                    var broken = new SaveNode { Id = 1, GameInfoId = 1, CgUnlockedIdsJson = "{not json" };
                    try
                    {
                        var brokenSample = projectOne.Invoke(null, new object[] { broken });
                        var brokenIds = (sampleType.GetProperty("CgUnlockedIds")?.GetValue(brokenSample)
                                         as IEnumerable<string>)?.ToList() ?? new List<string>();
                        details.Add($"  [OK]   malformed CgUnlockedIdsJson -> {brokenIds.Count} id(s), no exception");
                    }
                    catch (Exception ex)
                    {
                        var actualException = ex is System.Reflection.TargetInvocationException { InnerException: { } inner }
                            ? inner
                            : ex;
                        failures.Add($"{id}: a malformed CgUnlockedIdsJson made the projection throw "
                                   + $"{actualException.GetType().Name}; the column is free-form and must degrade, "
                                   + "not crash the sync.");
                        details.Add($"  [FAIL] malformed CG json threw {actualException.GetType().Name}");
                    }

                    // The label is the field the server matches a node on, so a node without a scene
                    // label must produce a null - NOT a readable substitute. DisplayName falls back to
                    // the snapshot description, the chapter and then the slot name, so carrying it
                    // here would hand the server a string that cannot match any node and cannot be
                    // told apart from a real label. A save that cannot be located is an honest
                    // "cannot be placed"; a save located by a guess is a player at the wrong node.
                    var unlabelled = new SaveNode
                    {
                        Id = 2,
                        GameInfoId = 77,
                        SlotName = "auto-7-LT1",
                        ChapterName = "第三章",
                        SnapshotDescription = "第一次遇见宁宁"
                    };

                    var unlabelledSample = projectOne.Invoke(null, new object[] { unlabelled });
                    var carriedLabel = sampleType.GetProperty("SceneLabel")?.GetValue(unlabelledSample) as string;
                    var displayedName = unlabelled.DisplayName;

                    details.Add($"  [{(carriedLabel is null ? "OK" : "FAIL")}]   node with no SceneLabel -> "
                              + $"SceneLabel={Format(carriedLabel)} (its DisplayName is '{displayedName}'; that string "
                              + "must NOT be substituted)");

                    if (carriedLabel is not null)
                    {
                        failures.Add($"{id}: a node with no SceneLabel projected SceneLabel='{carriedLabel}'. The "
                                   + "projection must not substitute a display name for a script label - the server "
                                   + "matches on that field and a substitute matches nothing while looking real.");
                    }
                }
            }

            if (project is null)
            {
                failures.Add($"{id}: SaveNodePositionProjector.Project(IEnumerable<SaveNode>) does not exist.");
                details.Add("  [FAIL] Project(IEnumerable<SaveNode>) not found");
            }
            else
            {
                var older = new SaveNode
                {
                    Id = 11, GameInfoId = 77, SceneLabel = "序章",
                    SaveModifiedTime = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
                };
                var newer = new SaveNode
                {
                    Id = 12, GameInfoId = 77, SceneLabel = "终章",
                    SaveModifiedTime = new DateTime(2026, 4, 14, 0, 0, 0, DateTimeKind.Utc)
                };
                var undated = new SaveNode { Id = 13, GameInfoId = 77, SceneLabel = "无时间戳" };

                object? projected = null;
                Exception? batchFailure = null;
                try
                {
                    projected = project.Invoke(null, new object[] { new List<SaveNode> { newer, undated, older } });
                }
                catch (Exception ex)
                {
                    batchFailure = ex;
                }

                if (batchFailure is not null)
                {
                    var actualException = batchFailure is System.Reflection.TargetInvocationException { InnerException: { } inner }
                        ? inner
                        : batchFailure;
                    failures.Add($"{id}: Project threw {actualException.GetType().Name}: {actualException.Message}");
                    details.Add($"  [FAIL] Project threw {actualException.GetType().Name}: {actualException.Message}");
                }
                else
                {
                    var ordered = (projected as System.Collections.IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
                    var labels = ordered
                        .Select(s => sampleType.GetProperty("SceneLabel")?.GetValue(s) as string ?? "(null)")
                        .ToList();

                    details.Add($"  [{(labels.Count == 3 ? "OK" : "FAIL")}]   Project(given newer, undated, older) -> "
                              + $"{labels.Count} sample(s): {string.Join(" -> ", labels)}");
                    details.Add("        story order: by save time ascending, undated nodes last");

                    if (labels.Count != 3)
                    {
                        failures.Add($"{id}: Project returned {labels.Count} samples for 3 nodes.");
                    }
                    else if (!labels.SequenceEqual(new[] { "序章", "终章", "无时间戳" }))
                    {
                        failures.Add($"{id}: Project produced the order [{string.Join(", ", labels)}]; the flowchart "
                                   + "sync needs story order (ascending save time, undated last), not insertion order.");
                    }
                }
            }
        }

        // ------------------------------------------------------------- A92.5 schema untouched
        details.Add(string.Empty);
        details.Add("--- A92.5 the EF model gains no entity for either feature ---");

        try
        {
            using var scope = context.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

            var entityNames = db.Model.GetEntityTypes()
                .Select(e => e.ClrType.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            details.Add($"  entities in the model ({entityNames.Count}): {string.Join(", ", entityNames)}");

            var reservedEntities = entityNames
                .Where(name => name.Contains("Flowchart", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Achievement", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (reservedEntities.Count > 0)
            {
                failures.Add($"{id}: the model contains {string.Join(", ", reservedEntities)}; the reservation must "
                           + "not force a schema change (the interface layer is not allowed to need a table).");
                details.Add($"  [FAIL] reserved-feature entity/entities present: {string.Join(", ", reservedEntities)}");
            }
            else
            {
                details.Add("  [OK]   no entity name mentions Flowchart or Achievement");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{id}: reading the EF model threw {ex.GetType().Name}: {ex.Message}");
            details.Add($"  [FAIL] {ex.GetType().Name}: {ex.Message}");
        }

        // --------------------------------------------------------------------------------- verdict
        details.Add(string.Empty);
        details.Add($"Honesty failures : {failures.Count}");
        foreach (var failure in failures)
        {
            details.Add($"  - {failure}");
        }

        if (failures.Count > 0)
        {
            details.Add("FAIL REASON: " + failures[0]);
            return Task.FromResult(CheckResult.Fail(
                    Id, Title, expected,
                    $"{failures.Count} failure(s) in the reserved layer's honesty")
                .With(details.ToArray()));
        }

        return Task.FromResult(CheckResult.Pass(
                Id, Title, expected,
                "0 implementations, 0 registrations, 0 placeholders, projection carries all 10 documented values, "
                + "0 new entities")
            .With(details.ToArray()));
    }

    /// <summary>Renders a value for the report without throwing on null.</summary>
    private static string Format(object? value) => value switch
    {
        null => "(null)",
        string text => $"'{text}'",
        DateTime time => time.ToString("yyyy-MM-dd HH:mm:ss"),
        _ => value.ToString() ?? "(null)"
    };
}
