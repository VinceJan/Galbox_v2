using Galbox.Acceptance.Support;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A90 - the two P2 features have a real interface layer (and only an interface layer).
///
/// Product source: <c>_product/Galbox-产品知识总纲.md</c> lines 173-174 -
///
/// <code>
/// | **P2** | 流程图追踪     | 🔧 第一版只预留接口，前端隐藏 |
/// | **P2** | 社区成就系统   | 🔧 第一版只预留接口，前端隐藏 |
/// </code>
///
/// and the two full specifications behind them (spec §6.1 for the flowchart, §6.2 for the
/// achievements). The audit that this check is written against found the opposite of "reserved":
/// <c>src/</c> contained <b>zero</b> occurrences of flowchart or achievement vocabulary, i.e. the
/// first version did not merely hide the features, it had nothing to hide. This check closes that
/// gap by pinning the shape of the reserved layer:
///
///   A90.1 the feature gate exists (<c>ReservedFeature</c>, <c>ReservedFeatureInfo</c>,
///         <c>IReservedFeatureCatalog</c>, <c>GalboxFeatureFlags</c>).
///   A90.2 the flowchart model exists and carries what spec §6.1 documents: node / edge / route /
///         ending geometry, the five node types, per-choice unlock conditions, per-route
///         achievement id, and the summary metadata (route count, ending count, estimate, version).
///   A90.3 the achievement model exists and carries what spec §6.2 documents: the five types, icon
///         plus locked icon, points, optional progress, rarity, the hidden flag, the summary, the
///         unlock notification whose title is 成就解锁！, and the two evidence kinds the product
///         owner chose in person (存档解析推断 + 进程内存监控).
///   A90.4 the two provider interfaces exist with the exact signatures of the documented endpoints
///         (<c>GET /api/v1/flowcharts/{game_id}</c>, <c>POST /api/v1/flowcharts/{game_id}/sync</c>,
///         <c>GET /api/v1/achievements/{game_id}</c>, <c>POST /api/v1/achievements/{game_id}/unlock</c>).
///   A90.5 the save-node seam exists: the reserved flowchart sync consumes a story-position sample
///         that is deliberately <b>not</b> the EF entity, and the adapter that projects a stored
///         <c>SaveNode</c> onto it exists in the assembly that already owns that glue.
///
/// Nothing here asserts that any of it works. There is no server, so there is nothing to work; A91
/// asserts that none of it is reachable from the UI, and A92 that nothing pretends otherwise.
/// </summary>
public sealed class A90ReservedInterfaceShapeCheck : IAcceptanceCheck
{
    /// <summary>The five node types of spec §6.1.</summary>
    private static readonly string[] NodeTypes = { "Start", "Route", "Branch", "Ending", "Event" };

    /// <summary>The five achievement types of spec §6.2.</summary>
    private static readonly string[] AchievementTypes = { "Story", "Normal", "Hidden", "Rare", "Community" };

    /// <inheritdoc />
    public string Id => "A90";

    /// <inheritdoc />
    public string Title => "reserved interface layer exists with the documented shape (flowchart + achievements)";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "Galbox.Core.Community defines the feature gate, the flowchart model (5 node types, "
            + "nodes/edges/routes/endings, per-choice unlock condition, per-route achievement id), the "
            + "achievement model (5 types, icon + locked icon, points, optional progress, rarity, hidden "
            + "flag, 成就解锁！ notification, 存档解析推断 + 进程内存监控 evidence kinds) and the provider "
            + "interfaces matching the documented endpoints; the story-position sample that the flowchart "
            + "sync consumes is not the EF entity, and Galbox.Services.Community projects a stored SaveNode "
            + "onto it";

        var details = new List<string>();
        var failures = new List<string>();
        const string id = "A90";

        details.Add($"Expectation source : _product/Galbox-产品知识总纲.md lines 173-174, §6.1, §6.2");
        details.Add($"Assembly under test: {ReservedInterfaceProbe.CoreAssembly} (feature gate + models + interfaces)");
        details.Add($"                      {ReservedInterfaceProbe.ServicesAssembly} (save-node -> story-position adapter)");

        // --------------------------------------------------------------- A90.1 the feature gate
        details.Add(string.Empty);
        details.Add("--- A90.1 feature gate ---");

        var reservedFeature = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Community}.ReservedFeature", details, failures, id);
        var reservedState = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Community}.ReservedFeatureState", details, failures, id);
        var info = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Community}.ReservedFeatureInfo", details, failures, id);
        var catalog = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Community}.IReservedFeatureCatalog", details, failures, id);
        var catalogImpl = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Community}.ReservedFeatureCatalog", details, failures, id);
        var flags = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Community}.GalboxFeatureFlags", details, failures, id);
        var serviceBase = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Community}.IReservedFeatureService", details, failures, id);
        var failureKind = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Community}.CommunityServiceFailureKind", details, failures, id);

        if (reservedFeature is not null)
        {
            ReservedInterfaceProbe.AssertEnumMembers(
                reservedFeature, new[] { "FlowchartTracking", "CommunityAchievements" }, details, failures, id);
        }

        if (reservedState is not null)
        {
            ReservedInterfaceProbe.AssertEnumMembers(reservedState, new[] { "InterfaceOnly" }, details, failures, id);
        }

        if (info is not null)
        {
            ReservedInterfaceProbe.AssertProperties(
                info,
                new[]
                {
                    "Feature", "DisplayName", "State", "IsImplemented", "IsEnabled",
                    "UnavailableReason", "RequiredServerCapabilities", "SpecReferences"
                },
                details, failures, id);
        }

        if (catalog is not null)
        {
            ReservedInterfaceProbe.AssertProperties(catalog, new[] { "All" }, details, failures, id);
            ReservedInterfaceProbe.AssertMethod(catalog, "Get", new[] { "ReservedFeature" }, details, failures, id);
            ReservedInterfaceProbe.AssertMethod(catalog, "IsEnabled", new[] { "ReservedFeature" }, details, failures, id);
        }

        if (catalogImpl is not null)
        {
            if (catalog is not null && !catalog.IsAssignableFrom(catalogImpl))
            {
                failures.Add($"{id}: ReservedFeatureCatalog does not implement IReservedFeatureCatalog.");
                details.Add("      [FAIL] ReservedFeatureCatalog does not implement IReservedFeatureCatalog");
            }

            var constructor = catalogImpl.GetConstructor(Type.EmptyTypes);
            details.Add(constructor is null
                ? "      [FAIL] no public parameterless constructor (the DI container cannot build it)"
                : "      [OK]   public parameterless constructor");

            if (constructor is null)
            {
                failures.Add($"{id}: ReservedFeatureCatalog has no public parameterless constructor.");
            }
        }

        if (flags is not null)
        {
            foreach (var fieldName in new[] { "FlowchartTrackingEnabled", "CommunityAchievementsEnabled" })
            {
                var field = flags.GetField(fieldName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (field is null)
                {
                    details.Add($"      [FAIL] no public static field {fieldName}");
                    failures.Add($"{id}: GalboxFeatureFlags has no public static field '{fieldName}'.");
                }
                else
                {
                    details.Add($"      [OK]   {fieldName} = {field.GetRawConstantValue()}");
                }
            }

            ReservedInterfaceProbe.AssertMethod(flags, "IsEnabled", new[] { "ReservedFeature" }, details, failures, id);
        }

        if (serviceBase is not null)
        {
            ReservedInterfaceProbe.AssertProperties(
                serviceBase, new[] { "Feature", "IsAvailable", "UnavailableReason" }, details, failures, id);
        }

        if (failureKind is not null)
        {
            // The two spec §6.1 error codes and the four spec §6.2 error semantics, plus the shared
            // "do not collapse a broken server into an empty answer" vocabulary the rest of the
            // product already uses for metadata sources.
            ReservedInterfaceProbe.AssertEnumMembers(
                failureKind,
                new[]
                {
                    "None", "NotConfigured", "InvalidRequest", "Unauthorized", "RateLimited",
                    "NetworkError", "HttpError", "MalformedResponse", "UpstreamError",
                    "NotFound", "GameNotSupported", "InvalidProgress", "AlreadyUnlocked", "TooFrequent"
                },
                details, failures, id);
        }

        // -------------------------------------------------------------- A90.2 flowchart model
        details.Add(string.Empty);
        details.Add("--- A90.2 flowchart model (spec §6.1) ---");

        var nodeType = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Flowcharts}.FlowchartNodeType", details, failures, id);
        var point = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Flowcharts}.FlowchartPoint", details, failures, id);
        var condition = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Flowcharts}.FlowchartChoiceCondition", details, failures, id);
        var choice = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Flowcharts}.FlowchartChoice", details, failures, id);
        var node = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Flowcharts}.FlowchartNode", details, failures, id);
        var edge = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Flowcharts}.FlowchartEdge", details, failures, id);
        var edgeStyle = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Flowcharts}.FlowchartEdgeStyle", details, failures, id);
        var route = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Flowcharts}.FlowchartRoute", details, failures, id);
        var metadata = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Flowcharts}.FlowchartMetadata", details, failures, id);
        var flowchart = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Flowcharts}.GameFlowchart", details, failures, id);

        if (nodeType is not null)
        {
            ReservedInterfaceProbe.AssertEnumMembers(nodeType, NodeTypes, details, failures, id);
        }

        if (point is not null)
        {
            ReservedInterfaceProbe.AssertProperties(point, new[] { "X", "Y" }, details, failures, id);
        }

        if (condition is not null)
        {
            ReservedInterfaceProbe.AssertProperties(condition, new[] { "Flag", "Value" }, details, failures, id);
        }

        if (choice is not null)
        {
            ReservedInterfaceProbe.AssertProperties(
                choice, new[] { "Id", "Text", "NextNodeId", "Condition" }, details, failures, id);
        }

        if (node is not null)
        {
            ReservedInterfaceProbe.AssertProperties(
                node,
                new[]
                {
                    "Id", "Label", "Type", "Position", "Chapter", "Description",
                    "Character", "RouteType", "EndingKind", "CgIds", "Choices"
                },
                details, failures, id);
        }

        if (edge is not null)
        {
            ReservedInterfaceProbe.AssertProperties(
                edge, new[] { "Id", "From", "To", "Label", "Style" }, details, failures, id);
        }

        if (edgeStyle is not null)
        {
            ReservedInterfaceProbe.AssertProperties(edgeStyle, new[] { "Color", "Dashed" }, details, failures, id);
        }

        if (route is not null)
        {
            ReservedInterfaceProbe.AssertProperties(
                route, new[] { "Id", "Name", "Color", "NodeIds", "AchievementId" }, details, failures, id);
        }

        if (metadata is not null)
        {
            ReservedInterfaceProbe.AssertProperties(
                metadata,
                new[] { "TotalRoutes", "TotalEndings", "EstimatedCompletionTime", "Version", "LastUpdated" },
                details, failures, id);
        }

        if (flowchart is not null)
        {
            ReservedInterfaceProbe.AssertProperties(
                flowchart,
                new[] { "GameId", "Version", "LastUpdated", "Metadata", "Nodes", "Edges", "Routes" },
                details, failures, id);
        }

        // ------------------------------------------------------------ A90.3 achievement model
        details.Add(string.Empty);
        details.Add("--- A90.3 achievement model (spec §6.2) ---");

        var achievementType = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Achievements}.AchievementType", details, failures, id);
        var evidenceKind = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Achievements}.AchievementEvidenceKind", details, failures, id);
        var definition = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Achievements}.AchievementDefinition", details, failures, id);
        var progress = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Achievements}.AchievementProgress", details, failures, id);
        var achievementView = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Achievements}.AchievementView", details, failures, id);
        var summary = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Achievements}.AchievementSummary", details, failures, id);
        var evidence = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Achievements}.AchievementEvidence", details, failures, id);
        var report = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Achievements}.AchievementReportRequest", details, failures, id);
        var notification = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Achievements}.AchievementUnlockNotification", details, failures, id);

        if (achievementType is not null)
        {
            ReservedInterfaceProbe.AssertEnumMembers(achievementType, AchievementTypes, details, failures, id);
        }

        if (evidenceKind is not null)
        {
            // The product owner's own two paths: 存档解析推断 and 进程内存监控. Manual declaration was
            // explicitly rejected ("不让玩家自己手动申报"), so it must not be expressible here.
            ReservedInterfaceProbe.AssertEnumMembers(
                evidenceKind, new[] { "Unknown", "SaveAnalysis", "ProcessMemory" }, details, failures, id);

            var manual = Enum.GetNames(evidenceKind).Where(n => n.Contains("Manual", StringComparison.Ordinal)).ToList();
            if (manual.Count > 0)
            {
                failures.Add($"{id}: AchievementEvidenceKind offers a manual-declaration kind "
                           + $"({string.Join(", ", manual)}), which the product owner rejected.");
            }
            else
            {
                details.Add("      [OK]   no manual-declaration evidence kind (rejected by the product owner)");
            }
        }

        if (definition is not null)
        {
            ReservedInterfaceProbe.AssertProperties(
                definition,
                new[]
                {
                    "Id", "GameId", "Name", "Description", "IconUrl", "LockedIconUrl", "Type",
                    "Points", "IsHidden", "Rarity", "ProgressTotal"
                },
                details, failures, id);
        }

        if (progress is not null)
        {
            ReservedInterfaceProbe.AssertProperties(progress, new[] { "Current", "Total", "Percent" }, details, failures, id);
        }

        if (achievementView is not null)
        {
            ReservedInterfaceProbe.AssertProperties(
                achievementView,
                new[] { "Definition", "IsUnlocked", "UnlockedAt", "Progress", "DisplayName", "DisplayDescription" },
                details, failures, id);
        }

        if (summary is not null)
        {
            ReservedInterfaceProbe.AssertProperties(
                summary,
                new[] { "TotalCount", "UnlockedCount", "TotalPoints", "UnlockedPoints", "CompletionPercent" },
                details, failures, id);
        }

        if (evidence is not null)
        {
            ReservedInterfaceProbe.AssertProperties(
                evidence,
                new[] { "Kind", "GameInfoId", "ObservedAtUtc", "SceneLabels", "CgIds" },
                details, failures, id);
        }

        if (report is not null)
        {
            ReservedInterfaceProbe.AssertProperties(
                report,
                new[] { "GameId", "UnlockedCgIds", "LastUnlockedAtUtc", "Evidence" },
                details, failures, id);
        }

        if (notification is not null)
        {
            ReservedInterfaceProbe.AssertProperties(
                notification,
                new[] { "AchievementId", "AchievementName", "IconUrl", "Points", "Title" },
                details, failures, id);
        }

        // ------------------------------------------------------------- A90.4 provider contracts
        details.Add(string.Empty);
        details.Add("--- A90.4 provider interfaces -> documented endpoints ---");

        var flowchartProvider = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Flowcharts}.IFlowchartProvider", details, failures, id);
        var achievementProvider = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Achievements}.IAchievementProvider", details, failures, id);
        var evidenceSource = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Achievements}.IAchievementEvidenceSource", details, failures, id);

        if (flowchartProvider is not null)
        {
            ReservedInterfaceProbe.AssertMethod(
                flowchartProvider, "GetFlowchartAsync", new[] { "FlowchartQuery", "CancellationToken" },
                details, failures, id, "Task`1");
            ReservedInterfaceProbe.AssertMethod(
                flowchartProvider, "SyncSavePositionsAsync", new[] { "FlowchartSyncRequest", "CancellationToken" },
                details, failures, id, "Task`1");
        }

        if (achievementProvider is not null)
        {
            ReservedInterfaceProbe.AssertMethod(
                achievementProvider, "GetAchievementsAsync", new[] { "String", "Boolean", "CancellationToken" },
                details, failures, id, "Task`1");
            ReservedInterfaceProbe.AssertMethod(
                achievementProvider, "ReportProgressAsync", new[] { "AchievementReportRequest", "CancellationToken" },
                details, failures, id, "Task`1");
            ReservedInterfaceProbe.AssertMethod(
                achievementProvider, "CreateShareCardAsync", new[] { "String", "CancellationToken" },
                details, failures, id, "Task`1");
        }

        if (evidenceSource is not null)
        {
            ReservedInterfaceProbe.AssertProperties(
                evidenceSource, new[] { "Kind", "IsEnabled", "DisabledReason" }, details, failures, id);
            ReservedInterfaceProbe.AssertMethod(
                evidenceSource, "CollectAsync", new[] { "Int32", "CancellationToken" },
                details, failures, id, "ValueTask`1");
        }

        // Both provider interfaces must inherit the availability contract; without it an
        // implementation could ship and never have to say whether it can answer.
        var serviceBaseName = $"{ReservedInterfaceProbe.Community}.IReservedFeatureService";
        foreach (var provider in new[] { flowchartProvider, achievementProvider })
        {
            if (provider is null)
            {
                continue;
            }

            var inherits = provider.GetInterfaces().Any(i => i.FullName == serviceBaseName);
            details.Add(inherits
                ? $"      [OK]   {provider.Name} : IReservedFeatureService"
                : $"      [FAIL] {provider.Name} does not inherit IReservedFeatureService");

            if (!inherits)
            {
                failures.Add($"{id}: {provider.Name} does not inherit IReservedFeatureService, so an "
                           + "implementation would not have to declare whether it can answer.");
            }
        }

        // ------------------------------------------------------------- A90.5 the save-node seam
        details.Add(string.Empty);
        details.Add("--- A90.5 the documented save-node seam (the whole point of the sync endpoint) ---");

        var sample = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Flowcharts}.StoryPositionSample", details, failures, id);
        var syncRequest = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Flowcharts}.FlowchartSyncRequest", details, failures, id);
        var positionMatch = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.Flowcharts}.FlowchartPositionMatch", details, failures, id);
        var projector = ReservedInterfaceProbe.Require(
            $"{ReservedInterfaceProbe.ServicesCommunity}.SaveNodePositionProjector",
            details, failures, id, ReservedInterfaceProbe.ServicesAssembly);

        if (sample is not null)
        {
            // These are exactly the stored SaveNode values that matter for locating a save on a
            // flowchart; the sample renames nothing except the entity itself.
            ReservedInterfaceProbe.AssertProperties(
                sample,
                new[]
                {
                    "GameInfoId", "SaveNodeId", "SlotName", "SceneLabel", "RouteName", "ChapterName",
                    "ChapterProgressPercent", "CgUnlockedIds", "PlayTimeSeconds", "SavedAtUtc"
                },
                details, failures, id);

            var saveNodeEntity = ReservedInterfaceProbe.Find("Galbox.Data.Entities.SaveNode", "Galbox.Data");
            if (saveNodeEntity is not null && sample.Assembly == saveNodeEntity.Assembly)
            {
                failures.Add($"{id}: StoryPositionSample lives in the data assembly; the reserved model "
                           + "must not depend on the EF entity.");
            }
            else if (saveNodeEntity is not null)
            {
                details.Add($"      [OK]   StoryPositionSample is defined in {sample.Assembly.GetName().Name}, "
                          + "not in the data assembly (the remote model stays free of EF)");
            }
        }

        if (syncRequest is not null)
        {
            ReservedInterfaceProbe.AssertProperties(syncRequest, new[] { "GameId", "Positions" }, details, failures, id);
        }

        if (positionMatch is not null)
        {
            ReservedInterfaceProbe.AssertProperties(
                positionMatch, new[] { "Position", "NodeId", "Confidence", "Reason" }, details, failures, id);
        }

        if (projector is not null)
        {
            ReservedInterfaceProbe.AssertMethod(
                projector, "Project", new[] { "IEnumerable`1" }, details, failures, id, "IReadOnlyList`1");
            ReservedInterfaceProbe.AssertMethod(
                projector, "ProjectOne", new[] { "SaveNode" }, details, failures, id, "StoryPositionSample");
        }

        // --------------------------------------------------------------------------------- verdict
        details.Add(string.Empty);
        details.Add($"Shape failures : {failures.Count}");
        foreach (var failure in failures)
        {
            details.Add($"  - {failure}");
        }

        if (failures.Count > 0)
        {
            details.Add("FAIL REASON: " + failures[0]);
            details.Add("The two P2 features are specified as 第一版只预留接口、前端隐藏 (spec lines 173-174). "
                      + "A reservation that does not exist cannot be built on later, so this is a real gap "
                      + "rather than a cosmetic one.");
            return Task.FromResult(CheckResult.Fail(
                    Id, Title, expected,
                    $"{failures.Count} shape failure(s) in the reserved interface layer")
                .With(details.ToArray()));
        }

        return Task.FromResult(CheckResult.Pass(
                Id, Title, expected,
                "feature gate + flowchart model + achievement model + provider interfaces + save-node seam all present with the documented shape")
            .With(details.ToArray()));
    }
}
