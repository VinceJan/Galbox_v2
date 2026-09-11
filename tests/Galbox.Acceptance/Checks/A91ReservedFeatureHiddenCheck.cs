using System.Text.RegularExpressions;
using Galbox.Acceptance.Support;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A91 - the two reserved features are OFF and have no door.
///
/// This is the check that makes "第一版只预留接口，前端隐藏" (spec lines 173-174) measurable instead
/// of a promise. It is a source-level check for the same reason A8 is one: whether a navigation key,
/// a page, a menu item or a button exists is a property of the source tree, and no runtime probe can
/// see something that was never written. The opposite defect - a feature that IS reachable while it
/// is not implemented - is the "假按钮" failure this project has hit before (spec §8.2 line 816), and
/// it is invisible to a runtime test too, because a dead button does nothing and therefore throws
/// nothing.
///
/// Asserted here:
///   A91.1 both feature flags are compile-time false (<c>GalboxFeatureFlags</c>).
///   A91.2 <c>IReservedFeatureCatalog</c> resolves from the app-shaped container and reports both
///         features as not implemented and not enabled, each with a non-empty explanation and a
///         non-empty list of the server capabilities the feature would need. A switch that cannot
///         say why it is off is not a switch.
///   A91.3 <c>NavigationService</c> exposes no key and no page for either feature, and the set of
///         keys it does expose is printed (so "the parser found nothing" cannot masquerade as a
///         pass).
///   A91.4 no page type named after either feature exists in <c>Galbox.App</c>.
///   A91.5 no file under <c>Views/</c>, no ViewModel and no <c>MainWindow</c> markup mentions either
///         feature. This is the assertion that covers a hand-written button that forgot to consult
///         the flag.
///   A91.6 <c>App.xaml.cs</c> registers no ViewModel or service whose name is one of the two
///         features.
/// </summary>
public sealed class A91ReservedFeatureHiddenCheck : IAcceptanceCheck
{
    /// <summary>
    /// Vocabulary that may not appear in anything the user can see or reach. Both languages are
    /// listed because the UI is Chinese while the type names are English.
    /// </summary>
    private static readonly string[] ReservedTokens = { "Flowchart", "Achievement", "流程图", "成就" };

    /// <inheritdoc />
    public string Id => "A91";

    /// <inheritdoc />
    public string Title => "reserved features are off and have no navigation door (前端隐藏)";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "GalboxFeatureFlags.FlowchartTrackingEnabled and .CommunityAchievementsEnabled are compile-time "
            + "false; the reserved feature catalog resolves from the app-shaped container and reports both "
            + "features as not implemented / not enabled with a reason and a list of missing server "
            + "capabilities; NavigationService has no key and no page for either feature; no page of either "
            + "name exists; and no file under Views/, no ViewModel and no MainWindow markup mentions either "
            + "feature at all";

        var details = new List<string>();
        var failures = new List<string>();
        const string id = "A91";

        var repoRoot = RepoLocator.FindRepoRoot();
        if (repoRoot is null)
        {
            details.Add($"FAIL REASON: could not find {RepoLocator.SolutionMarker} above '{AppContext.BaseDirectory}'.");
            return Task.FromResult(CheckResult.Fail(Id, Title, expected, "repository root not found")
                .With(details.ToArray()));
        }

        details.Add($"Repository root : {repoRoot}");
        details.Add($"Reserved tokens : {{{string.Join(", ", ReservedTokens)}}} (case-insensitive)");

        // ------------------------------------------------------------------ A91.1 the flags
        details.Add(string.Empty);
        details.Add("--- A91.1 feature flags are off ---");

        var flags = ReservedInterfaceProbe.Find($"{ReservedInterfaceProbe.Community}.GalboxFeatureFlags");
        var reservedFeatureType = ReservedInterfaceProbe.Find($"{ReservedInterfaceProbe.Community}.ReservedFeature");

        if (flags is null)
        {
            failures.Add($"{id}: {ReservedInterfaceProbe.Community}.GalboxFeatureFlags does not exist, "
                       + "so there is no switch to be off.");
            details.Add($"  [MISSING] {ReservedInterfaceProbe.Community}.GalboxFeatureFlags");
        }
        else
        {
            details.Add($"  [OK]      {flags.FullName}");
        }

        // Compile-time proof first: a const field cannot be flipped by a settings row, a database
        // value or an environment variable, which is exactly what "first version only" means.
        foreach (var fieldName in new[] { "FlowchartTrackingEnabled", "CommunityAchievementsEnabled" })
        {
            if (flags is null)
            {
                failures.Add($"{id}: cannot verify {fieldName} - GalboxFeatureFlags is missing.");
                continue;
            }

            var field = flags.GetField(fieldName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (field is null)
            {
                failures.Add($"{id}: GalboxFeatureFlags.{fieldName} does not exist.");
                details.Add($"  [FAIL]    {fieldName} is missing");
                continue;
            }

            var value = field.GetRawConstantValue();
            var isFalse = value is bool b && !b;
            details.Add($"  {(isFalse ? "[OK]     " : "[FAIL]   ")} {fieldName} = {value}"
                      + (field.IsLiteral ? " (const - requires a code change to enable)" : " (NOT const - could be flipped at run time)"));

            if (!isFalse)
            {
                failures.Add($"{id}: GalboxFeatureFlags.{fieldName} is {value}; the first version must ship it off.");
            }
            else if (!field.IsLiteral)
            {
                failures.Add($"{id}: GalboxFeatureFlags.{fieldName} is not a compile-time constant, so the "
                           + "feature could become visible without a code change.");
            }
        }

        if (flags is not null && reservedFeatureType is not null)
        {
            var isEnabled = flags.GetMethod("IsEnabled", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (isEnabled is null)
            {
                failures.Add($"{id}: GalboxFeatureFlags.IsEnabled(ReservedFeature) does not exist.");
            }
            else
            {
                foreach (var name in Enum.GetNames(reservedFeatureType))
                {
                    var value = Enum.Parse(reservedFeatureType, name);
                    var enabled = isEnabled.Invoke(null, new[] { value });
                    details.Add($"  {(enabled is false ? "[OK]     " : "[FAIL]   ")} IsEnabled({name}) = {enabled}");
                    if (enabled is not false)
                    {
                        failures.Add($"{id}: GalboxFeatureFlags.IsEnabled({name}) returned {enabled}, expected false.");
                    }
                }
            }
        }

        // ------------------------------------------------------------- A91.2 the catalog honestly off
        details.Add(string.Empty);
        details.Add("--- A91.2 the reserved feature catalog, resolved from the app-shaped container ---");

        var catalogType = ReservedInterfaceProbe.Find($"{ReservedInterfaceProbe.Community}.IReservedFeatureCatalog");
        if (catalogType is null)
        {
            failures.Add($"{id}: {ReservedInterfaceProbe.Community}.IReservedFeatureCatalog does not exist.");
            details.Add($"  [MISSING] {ReservedInterfaceProbe.Community}.IReservedFeatureCatalog");
        }
        else
        {
            object? catalog = null;
            Exception? resolutionFailure = null;
            try
            {
                catalog = context.Services.GetService(catalogType);
            }
            catch (Exception ex)
            {
                resolutionFailure = ex;
            }

            details.Add($"  resolved type : {catalog?.GetType().FullName ?? "(null)"}");

            if (resolutionFailure is not null)
            {
                failures.Add($"{id}: resolving IReservedFeatureCatalog threw {resolutionFailure.GetType().Name}: "
                           + resolutionFailure.Message);
                details.Add($"  [FAIL] {resolutionFailure.GetType().Name}: {resolutionFailure.Message}");
            }
            else if (catalog is null)
            {
                failures.Add($"{id}: IReservedFeatureCatalog did not resolve; App.xaml.cs (mirrored by "
                           + "AcceptanceContainer) must register the catalog so the switch has one authority.");
                details.Add("  [FAIL] the container returned null - the switch is documented but not wired.");
            }
            else if (reservedFeatureType is null)
            {
                failures.Add($"{id}: ReservedFeature enum is missing, so the catalog cannot be interrogated.");
            }
            else
            {
                var getMethod = catalogType.GetMethod("Get");
                foreach (var name in Enum.GetNames(reservedFeatureType))
                {
                    var feature = Enum.Parse(reservedFeatureType, name);
                    var info = getMethod?.Invoke(catalog, new[] { feature });

                    if (info is null)
                    {
                        failures.Add($"{id}: IReservedFeatureCatalog.Get({name}) returned null.");
                        details.Add($"  [FAIL] Get({name}) -> null");
                        continue;
                    }

                    var infoType = info.GetType();
                    var displayName = Read<string>(infoType, info, "DisplayName");
                    var isImplemented = Read<bool>(infoType, info, "IsImplemented");
                    var isEnabledValue = Read<bool>(infoType, info, "IsEnabled");
                    var reason = Read<string>(infoType, info, "UnavailableReason");
                    var state = Read<object>(infoType, info, "State")?.ToString();
                    var serverNeeds = Read<IEnumerable<string>>(infoType, info, "RequiredServerCapabilities")?.ToList()
                                   ?? new List<string>();
                    var specRefs = Read<IEnumerable<string>>(infoType, info, "SpecReferences")?.ToList()
                                ?? new List<string>();

                    details.Add(string.Empty);
                    details.Add($"  --- {name} ---");
                    details.Add($"      DisplayName        : {displayName}");
                    details.Add($"      State              : {state}");
                    details.Add($"      IsImplemented      : {isImplemented}");
                    details.Add($"      IsEnabled          : {isEnabledValue}");
                    details.Add($"      UnavailableReason  : {reason}");
                    details.Add($"      Required server    : {serverNeeds.Count} item(s)");
                    foreach (var need in serverNeeds)
                    {
                        details.Add($"          - {need}");
                    }

                    details.Add($"      SpecReferences     : {specRefs.Count} item(s)");
                    foreach (var reference in specRefs)
                    {
                        details.Add($"          - {reference}");
                    }

                    if (isEnabledValue)
                    {
                        failures.Add($"{id}: the catalog reports {name} as ENABLED while nothing implements it.");
                    }

                    if (isImplemented)
                    {
                        failures.Add($"{id}: the catalog reports {name} as IMPLEMENTED; nothing may claim that "
                                   + "before a server exists.");
                    }

                    if (string.IsNullOrWhiteSpace(reason))
                    {
                        failures.Add($"{id}: the catalog gives no UnavailableReason for {name}; a switch that "
                                   + "cannot say why it is off is not a switch.");
                    }
                    else if (reason!.Length <= 20)
                    {
                        failures.Add($"{id}: the UnavailableReason for {name} is {reason.Length} characters long, "
                                   + "which is not an explanation.");
                    }

                    if (serverNeeds.Count < 2)
                    {
                        failures.Add($"{id}: {name} lists {serverNeeds.Count} required server capability/ies; the "
                                   + "gap that blocks the feature has to be written down, not remembered.");
                    }

                    if (specRefs.Count == 0)
                    {
                        failures.Add($"{id}: {name} cites no spec reference.");
                    }
                }
            }
        }

        // ------------------------------------------------------------ A91.3 no navigation key
        details.Add(string.Empty);
        details.Add("--- A91.3 NavigationService exposes no door for either feature ---");

        var navigationSourcePath = Path.Combine(
            RepoLocator.AppProject(repoRoot), "Services", "NavigationService.cs");

        var navigationKeys = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!File.Exists(navigationSourcePath))
        {
            failures.Add($"{id}: {navigationSourcePath} not found.");
            details.Add($"  [FAIL] {navigationSourcePath} does not exist");
        }
        else
        {
            var navigationSource = File.ReadAllText(navigationSourcePath);
            var mappings = Regex.Matches(
                navigationSource,
                @"\{\s*""(?<key>[^""]+)""\s*,\s*typeof\(\s*(?<page>[A-Za-z_][A-Za-z0-9_]*)\s*\)\s*\}");

            if (mappings.Count == 0)
            {
                // Same guard A8.1 uses: a parse that found nothing must not read as "found nothing wrong".
                failures.Add($"{id}: NavigationService._pageMapping could not be parsed (0 entries).");
                details.Add("  [FAIL] 0 navigation keys parsed - this check cannot conclude anything");
            }

            foreach (Match mapping in mappings)
            {
                var key = mapping.Groups["key"].Value;
                var page = mapping.Groups["page"].Value;
                navigationKeys[key] = page;

                var offending = ReservedTokens
                    .Where(token => key.Contains(token, StringComparison.OrdinalIgnoreCase)
                                 || page.Contains(token, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (offending.Count > 0)
                {
                    failures.Add($"{id}: NavigationService maps key '{key}' -> {page}, which is a door to a "
                               + $"reserved feature (matched {string.Join(", ", offending)}).");
                    details.Add($"  [FAIL] {key} -> {page}");
                }
                else
                {
                    details.Add($"  [OK]   {key} -> {page}");
                }
            }

            details.Add($"  keys exposed by the shipping app: {navigationKeys.Count} "
                      + $"{{{string.Join(", ", navigationKeys.Keys.OrderBy(k => k, StringComparer.Ordinal))}}}");
        }

        // ---------------------------------------------------------------- A91.4 no page type
        details.Add(string.Empty);
        details.Add("--- A91.4 no page class named after either feature exists ---");

        var appAssembly = typeof(Galbox.App.App).Assembly;
        var candidatePageNames = ReservedInterfaceProbe.GetLoadableTypes(appAssembly)
            .Where(t => t.Namespace == "Galbox.App.Views" && t.IsClass)
            .Select(t => t.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        details.Add($"  pages in Galbox.App.Views ({candidatePageNames.Count}): {string.Join(", ", candidatePageNames)}");

        var reservedPages = candidatePageNames
            .Where(name => ReservedTokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (reservedPages.Count > 0)
        {
            failures.Add($"{id}: page type(s) {string.Join(", ", reservedPages)} exist for a feature that has "
                       + "no implementation.");
        }

        foreach (var probeName in new[] { "FlowchartPage", "AchievementPage", "FlowchartTrackingPage", "AchievementsPage" })
        {
            var type = Type.GetType($"Galbox.App.Views.{probeName}, {appAssembly.GetName().Name}");
            details.Add($"  {(type is null ? "[OK]   " : "[FAIL] ")} Galbox.App.Views.{probeName} exists: {type is not null}");
            if (type is not null)
            {
                failures.Add($"{id}: Galbox.App.Views.{probeName} exists, but the feature must not be reachable.");
            }
        }

        // ------------------------------------------------- A91.5 nothing in the UI mentions them
        details.Add(string.Empty);
        details.Add("--- A91.5 no view, ViewModel or MainWindow markup mentions either feature ---");

        var uiFiles = new List<string>();
        uiFiles.AddRange(RepoLocator.EnumerateViewFiles(RepoLocator.ViewsFolder(repoRoot), "*.xaml"));
        uiFiles.AddRange(RepoLocator.EnumerateViewFiles(RepoLocator.ViewsFolder(repoRoot), "*.xaml.cs"));

        var viewModelsFolder = Path.Combine(RepoLocator.AppProject(repoRoot), "ViewModels");
        if (Directory.Exists(viewModelsFolder))
        {
            uiFiles.AddRange(Directory
                .EnumerateFiles(viewModelsFolder, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                         && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var name in new[] { "MainWindow.xaml", "MainWindow.xaml.cs" })
        {
            var path = Path.Combine(RepoLocator.AppProject(repoRoot), name);
            if (File.Exists(path))
            {
                uiFiles.Add(path);
            }
        }

        details.Add($"  UI files scanned: {uiFiles.Count} (Views/**, ViewModels/**, MainWindow.xaml[.cs])");

        var uiFailures = 0;
        foreach (var path in uiFiles)
        {
            var text = File.ReadAllText(path);
            var hits = ReservedTokens
                .Where(token => text.Contains(token, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (hits.Count == 0)
            {
                continue;
            }

            uiFailures++;
            var relative = Path.GetRelativePath(repoRoot, path);
            foreach (var hit in hits)
            {
                var line = text
                    .Split('\n')
                    .Select((content, index) => (content, index))
                    .FirstOrDefault(entry => entry.content.Contains(hit, StringComparison.OrdinalIgnoreCase));

                details.Add($"  [FAIL] {relative}:{line.index + 1} mentions '{hit}': {line.content.Trim()}");
            }

            failures.Add($"{id}: {Path.GetRelativePath(repoRoot, path)} mentions {string.Join(", ", hits)} - a "
                       + "user-visible surface for a feature that is not implemented.");
        }

        if (uiFailures == 0)
        {
            details.Add($"  [OK]   0 of {uiFiles.Count} UI file(s) mention any of {{{string.Join(", ", ReservedTokens)}}}");
        }

        // --------------------------------------------------- A91.6 nothing registers them in DI
        details.Add(string.Empty);
        details.Add("--- A91.6 App.xaml.cs registers no service or ViewModel for either feature ---");

        var appSourcePath = Path.Combine(RepoLocator.AppProject(repoRoot), "App.xaml.cs");
        if (!File.Exists(appSourcePath))
        {
            failures.Add($"{id}: {appSourcePath} not found.");
        }
        else
        {
            var appSource = File.ReadAllText(appSourcePath);
            var registrations = Regex.Matches(
                appSource,
                @"Add(?:Transient|Singleton|Scoped)\s*<\s*(?<type>[A-Za-z_][A-Za-z0-9_]*)\s*(?:,|>)");

            details.Add($"  registrations parsed: {registrations.Count}");

            if (registrations.Count == 0)
            {
                failures.Add($"{id}: no DI registration could be parsed out of App.xaml.cs.");
                details.Add("  [FAIL] 0 registrations parsed - this check cannot conclude anything");
            }

            foreach (Match registration in registrations)
            {
                var typeName = registration.Groups["type"].Value;
                var offending = ReservedTokens
                    .Where(token => typeName.Contains(token, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (offending.Count > 0)
                {
                    failures.Add($"{id}: App.xaml.cs registers '{typeName}' for a reserved feature "
                               + $"(matched {string.Join(", ", offending)}).");
                    details.Add($"  [FAIL] {typeName}");
                }
            }

            var registeredNames = registrations
                .Select(m => m.Groups["type"].Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            details.Add($"  registered types: {string.Join(", ", registeredNames)}");
            details.Add($"  [OK]   none of the {registeredNames.Count} registered type name(s) is a reserved feature");
        }

        // --------------------------------------------------------------------------------- verdict
        details.Add(string.Empty);
        details.Add($"Hidden-ness failures : {failures.Count}");
        foreach (var failure in failures)
        {
            details.Add($"  - {failure}");
        }

        if (failures.Count > 0)
        {
            details.Add("FAIL REASON: " + failures[0]);
            return Task.FromResult(CheckResult.Fail(
                    Id, Title, expected,
                    $"{failures.Count} failure(s); {navigationKeys.Count} navigation key(s) exposed, "
                    + $"{candidatePageNames.Count} page(s) present")
                .With(details.ToArray()));
        }

        return Task.FromResult(CheckResult.Pass(
                Id, Title, expected,
                $"both flags const false, catalog resolves and reports both features off with a reason, "
                + $"{navigationKeys.Count} navigation key(s) none reserved, {candidatePageNames.Count} page(s) none "
                + $"reserved, {uiFiles.Count} UI file(s) with 0 mentions")
            .With(details.ToArray()));
    }

    /// <summary>
    /// Reads a property off a reflected object, tolerating a missing property (which A90 already
    /// reports by name) instead of throwing and turning a verdict into an ERROR.
    /// </summary>
    private static T? Read<T>(Type type, object instance, string propertyName)
    {
        var property = type.GetProperty(propertyName);
        if (property is null)
        {
            return default;
        }

        var value = property.GetValue(instance);
        return value is T typed ? typed : default;
    }
}
