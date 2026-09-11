using System.Text.RegularExpressions;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A8 - Navigation, page and XAML resource completeness.
///
/// This is the regression guard for the defect class the audit called "implemented but no door":
/// a feature whose engine is complete and registered in dependency injection, but which has no
/// page, no navigation key and no menu item - so no user can ever reach it. It is a source-level
/// check on purpose: a runtime test can never see a missing XAML file.
///
/// Sub-checks (every one must hold):
///   A8.1 every navigation key in NavigationService maps to a page type that really exists
///   A8.2 every ViewModel registered in App.xaml.cs is referenced by a view in Views/
///        (a ViewModel with no view is unreachable; known exceptions must be whitelisted with
///         a written reason, and the whitelist is printed so it cannot hide anything silently)
///   A8.3 every page in Views/ is reachable: it has a navigation key, or some source file
///        navigates to its type explicitly
///   A8.4 every converter a view references is registered in that view's own ResourceDictionary
///        or in App.xaml (x:Bind resolves converters through LookupConverter, which returns null
///         for an unregistered key and then throws while converting)
///   A8.5 every navigation menu item in MainWindow.xaml carries a Tag that is a known key
/// </summary>
public sealed class A8NavigationPageCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A8";

    /// <inheritdoc />
    public string Title => "Navigation, page and XAML resource completeness (no feature without a door)";

    /// <summary>
    /// ViewModels that intentionally have no view. Each entry needs a written reason; the list is
    /// printed on every run so an entry cannot be added silently.
    /// </summary>
    private static readonly (string ViewModel, string Reason)[] Whitelist = Array.Empty<(string, string)>();

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "every navigation key resolves to an existing page, every DI-registered "
                     + "ViewModel is referenced by a view (or whitelisted with a reason), every page "
                     + "is reachable, every converter a view uses is registered, and every "
                     + "MainWindow menu item maps to a navigation key";

        var details = new List<string>();
        var failures = new List<string>();

        var repoRoot = RepoLocator.FindRepoRoot();
        if (repoRoot is null)
        {
            details.Add($"FAIL REASON: could not find {RepoLocator.SolutionMarker} above '{AppContext.BaseDirectory}'.");
            return Task.FromResult(CheckResult.Fail(Id, Title, expected, "repository root not found")
                .With(details.ToArray()));
        }

        var viewsFolder = RepoLocator.ViewsFolder(repoRoot);
        var appXamlPath = Path.Combine(RepoLocator.AppProject(repoRoot), "App.xaml");
        var appSourcePath = Path.Combine(RepoLocator.AppProject(repoRoot), "App.xaml.cs");
        var navigationSourcePath = Path.Combine(RepoLocator.AppProject(repoRoot), "Services", "NavigationService.cs");
        var mainWindowXamlPath = Path.Combine(RepoLocator.AppProject(repoRoot), "MainWindow.xaml");

        details.Add($"Repository root      : {repoRoot}");
        details.Add($"Views folder         : {viewsFolder} (exists: {Directory.Exists(viewsFolder)})");

        var viewXamlFiles = RepoLocator.EnumerateViewFiles(viewsFolder, "*.xaml");
        var viewSourceFiles = RepoLocator.EnumerateViewFiles(viewsFolder, "*.xaml.cs");
        details.Add($"View XAML files      : {viewXamlFiles.Count}");
        details.Add($"View code-behind files: {viewSourceFiles.Count}");

        // ---------------------------------------------------------------- A8.1 navigation keys
        details.Add(string.Empty);
        details.Add("--- A8.1 navigation keys -> page types ---");

        var navigationSource = File.ReadAllText(navigationSourcePath);
        var mappingMatches = Regex.Matches(
            navigationSource,
            @"\{\s*""(?<key>[^""]+)""\s*,\s*typeof\(\s*(?<page>[A-Za-z_][A-Za-z0-9_]*)\s*\)\s*\}");

        if (mappingMatches.Count == 0)
        {
            failures.Add("A8.1: NavigationService._pageMapping could not be parsed (0 entries found).");
        }

        var navigationKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        var appAssembly = typeof(Galbox.App.App).Assembly;

        foreach (Match match in mappingMatches)
        {
            var key = match.Groups["key"].Value;
            var pageTypeName = match.Groups["page"].Value;
            navigationKeys[key] = pageTypeName;

            var pageType = Type.GetType($"Galbox.App.Views.{pageTypeName}, {appAssembly.GetName().Name}");
            if (pageType is null)
            {
                failures.Add($"A8.1: navigation key '{key}' maps to '{pageTypeName}', which does not exist.");
                details.Add($"  [FAIL] {key,-16} -> {pageTypeName} (type not found in Galbox.App)");
            }
            else
            {
                details.Add($"  [OK]   {key,-16} -> {pageType.FullName}");
            }
        }

        // ------------------------------------------------------------- A8.2 ViewModels -> views
        details.Add(string.Empty);
        details.Add("--- A8.2 ViewModels registered in App.xaml.cs -> view that references them ---");

        var appSource = File.ReadAllText(appSourcePath);
        var viewModelMatches = Regex.Matches(
            appSource,
            @"Add(?:Transient|Singleton|Scoped)\s*<\s*(?<vm>[A-Za-z_][A-Za-z0-9_]*ViewModel)\s*>\s*\(\s*\)");

        var registeredViewModels = viewModelMatches
            .Select(m => m.Groups["vm"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        details.Add($"  Registered ViewModels: {registeredViewModels.Count}");
        foreach (var (viewModel, reason) in Whitelist)
        {
            details.Add($"  [WHITELIST] {viewModel}: {reason}");
        }

        var viewTexts = viewXamlFiles.Concat(viewSourceFiles)
            .ToDictionary(path => path, File.ReadAllText);

        foreach (var viewModel in registeredViewModels)
        {
            if (Whitelist.Any(w => w.ViewModel == viewModel))
            {
                details.Add($"  [SKIP] {viewModel} (whitelisted)");
                continue;
            }

            var referencing = viewTexts
                .Where(kv => kv.Value.Contains(viewModel, StringComparison.Ordinal))
                .Select(kv => Path.GetFileName(kv.Key))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            if (referencing.Count == 0)
            {
                failures.Add($"A8.2: ViewModel '{viewModel}' is registered in DI but no file under Views/ references it - the feature has no user-visible door.");
                details.Add($"  [FAIL] {viewModel,-26} referenced by: (nothing under Views/)");
            }
            else
            {
                details.Add($"  [OK]   {viewModel,-26} referenced by: {string.Join(", ", referencing)}");
            }
        }

        // ---------------------------------------------------------------- A8.3 page reachability
        details.Add(string.Empty);
        details.Add("--- A8.3 every page in Views/ is reachable ---");

        var sourceFiles = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var allSourceText = string.Join("\n", sourceFiles.Select(File.ReadAllText));

        foreach (var xamlPath in viewXamlFiles)
        {
            var xaml = viewTexts.TryGetValue(xamlPath, out var cached) ? cached : File.ReadAllText(xamlPath);
            var classMatch = Regex.Match(xaml, @"x:Class=""Galbox\.App\.Views\.(?<page>[A-Za-z_][A-Za-z0-9_]*)""");
            if (!classMatch.Success)
            {
                continue;
            }

            var pageName = classMatch.Groups["page"].Value;
            var inNavigationMap = navigationKeys.Values.Contains(pageName, StringComparer.Ordinal);
            var explicitlyNavigated = Regex.IsMatch(
                allSourceText,
                $@"typeof\(\s*{Regex.Escape(pageName)}\s*\)");

            if (inNavigationMap || explicitlyNavigated)
            {
                details.Add($"  [OK]   {pageName,-24} navigationKey={inNavigationMap}, explicitNavigateTo={explicitlyNavigated}");
            }
            else
            {
                failures.Add($"A8.3: page '{pageName}' is not reachable - it has no navigation key and no source file navigates to its type.");
                details.Add($"  [FAIL] {pageName,-24} currently unreachable");
            }
        }

        // ------------------------------------------------------------- A8.4 converter resources
        details.Add(string.Empty);
        details.Add("--- A8.4 converters referenced by a view are registered ---");

        var appXaml = File.ReadAllText(appXamlPath);
        var appKeys = ResourceKeys(appXaml);
        details.Add($"  App.xaml resource keys: {appKeys.Count}");

        var converterReferences = 0;
        foreach (var xamlPath in viewXamlFiles.Append(mainWindowXamlPath))
        {
            if (!File.Exists(xamlPath))
            {
                continue;
            }

            var xaml = File.ReadAllText(xamlPath);
            var pageKeys = ResourceKeys(xaml);
            var referenced = Regex
                .Matches(xaml, @"(?:StaticResource|LookupConverter\("")\s*(?<key>[A-Za-z0-9_]+)")
                .Select(m => m.Groups["key"].Value)
                .Where(key => key.EndsWith("Converter", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();

            var missing = referenced
                .Where(key => !pageKeys.Contains(key) && !appKeys.Contains(key))
                .ToList();

            converterReferences += referenced.Count;

            if (missing.Count > 0)
            {
                failures.Add($"A8.4: {Path.GetFileName(xamlPath)} references unregistered converter(s): {string.Join(", ", missing)}.");
                details.Add($"  [FAIL] {Path.GetFileName(xamlPath),-26} missing: {string.Join(", ", missing)}");
            }
            else if (referenced.Count > 0)
            {
                details.Add($"  [OK]   {Path.GetFileName(xamlPath),-26} {referenced.Count} converter reference(s), all registered");
            }
        }

        details.Add($"  Total converter references checked: {converterReferences}");

        // ------------------------------------------------------- A8.5 MainWindow navigation tags
        details.Add(string.Empty);
        details.Add("--- A8.5 MainWindow.xaml menu items -> navigation keys ---");

        if (File.Exists(mainWindowXamlPath))
        {
            var mainWindowXaml = File.ReadAllText(mainWindowXamlPath);
            var tags = Regex.Matches(mainWindowXaml, @"Tag=""(?<tag>[^""]+)""")
                .Select(m => m.Groups["tag"].Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(tag => tag, StringComparer.Ordinal)
                .ToList();

            if (tags.Count == 0)
            {
                failures.Add("A8.5: MainWindow.xaml declares no navigation menu item with a Tag.");
            }

            foreach (var tag in tags)
            {
                if (navigationKeys.ContainsKey(tag))
                {
                    details.Add($"  [OK]   menu Tag '{tag}' -> {navigationKeys[tag]}");
                }
                else
                {
                    failures.Add($"A8.5: MainWindow.xaml menu item Tag '{tag}' is not a known navigation key.");
                    details.Add($"  [FAIL] menu Tag '{tag}' has no navigation key");
                }
            }
        }
        else
        {
            failures.Add("A8.5: MainWindow.xaml not found.");
        }

        // ---------------------------------------------------------------------------- verdict
        details.Add(string.Empty);
        details.Add($"Sub-check failures : {failures.Count}");
        foreach (var failure in failures)
        {
            details.Add($"  - {failure}");
        }

        if (failures.Count > 0)
        {
            return Task.FromResult(CheckResult.Fail(
                    Id, Title, expected,
                    $"{failures.Count} completeness failure(s) across {viewXamlFiles.Count} view(s) and {registeredViewModels.Count} ViewModel(s)")
                .With(details.ToArray()));
        }

        return Task.FromResult(CheckResult.Pass(
                Id, Title, expected,
                $"{navigationKeys.Count} navigation keys, {registeredViewModels.Count} ViewModels, "
              + $"{viewXamlFiles.Count} views, {converterReferences} converter reference(s) all consistent")
            .With(details.ToArray()));
    }

    /// <summary>Collects every <c>x:Key</c> declared in a XAML file.</summary>
    private static HashSet<string> ResourceKeys(string xaml)
    {
        return Regex.Matches(xaml, @"x:Key=""(?<key>[^""]+)""")
            .Select(m => m.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}
