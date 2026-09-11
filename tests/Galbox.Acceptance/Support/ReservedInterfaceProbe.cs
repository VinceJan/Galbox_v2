using System.Reflection;

namespace Galbox.Acceptance.Support;

/// <summary>
/// Reflection helpers for the two reserved features (checks A90-A92).
///
/// <b>Why reflection and not a direct type reference.</b> A90/A91/A92 are the checks of a feature
/// that is being *reserved*: the interface layer does not exist yet in the revision they are
/// written against. A harness that named <c>Galbox.Core.Community.IReservedFeatureCatalog</c>
/// directly would not compile there at all, and the required "run it first, watch it FAIL, then
/// implement, watch it PASS" sequence would be impossible from a single harness revision.
/// Resolving every type by name keeps the harness compiling on both revisions, which is the same
/// reason <c>AcceptanceContainer.RegisterIfPresent</c> registers the reserved feature catalog by
/// name rather than by type.
///
/// The cost of that choice is honest and bounded: the assertions are still exact - a type that is
/// missing, an enum member that was renamed, a method whose signature drifted, all of them are
/// reported by name - they are simply resolved at run time instead of at compile time.
///
/// The compile-time reference to the same contract does exist, and it is a stronger one than a
/// test-side reference would be: <c>src/Galbox.App/App.xaml.cs</c> registers
/// <c>IReservedFeatureCatalog</c> / <c>ReservedFeatureCatalog</c> by their strong types, so the
/// shipping application itself cannot build unless those types are present with that exact name.
/// </summary>
internal static class ReservedInterfaceProbe
{
    /// <summary>Assembly that carries the whole reserved interface layer.</summary>
    public const string CoreAssembly = "Galbox.Core";

    /// <summary>Assembly that carries the save-node -> story-position adapter.</summary>
    public const string ServicesAssembly = "Galbox.Services";

    /// <summary>Root namespace of the reserved interface layer.</summary>
    public const string Community = "Galbox.Core.Community";

    /// <summary>Namespace of the reserved flowchart tracking types (spec §6.1).</summary>
    public const string Flowcharts = Community + ".Flowcharts";

    /// <summary>Namespace of the reserved community achievement types (spec §6.2).</summary>
    public const string Achievements = Community + ".Achievements";

    /// <summary>Namespace of the save-node -> story-position adapter.</summary>
    public const string ServicesCommunity = "Galbox.Services.Community";

    /// <summary>Every assembly of the shipping product. A type outside this set is not shipped.</summary>
    public static readonly string[] ShippingAssemblies =
    {
        "Galbox.Core", "Galbox.Data", "Galbox.Services", "Galbox.App"
    };

    /// <summary>Resolves a type by full name inside one assembly; null when it is not there.</summary>
    public static Type? Find(string fullName, string assemblyName = CoreAssembly) =>
        Type.GetType($"{fullName}, {assemblyName}", throwOnError: false);

    /// <summary>
    /// Resolves a type by full name, recording the outcome in <paramref name="details"/> and
    /// <paramref name="failures"/> so a missing type is reported exactly once, by name, with the
    /// namespace it was expected in.
    /// </summary>
    public static Type? Require(
        string fullName,
        ICollection<string> details,
        ICollection<string> failures,
        string checkId,
        string assemblyName = CoreAssembly)
    {
        var type = Find(fullName, assemblyName);

        if (type is null)
        {
            // Always print the assembly-qualified name: "the type is not there" is only actionable
            // when the report says where it was looked for.
            details.Add($"  [MISSING] {fullName}  (looked in {assemblyName})");
            failures.Add($"{checkId}: type '{fullName}' does not exist in assembly '{assemblyName}'.");
            return null;
        }

        var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class";
        details.Add($"  [OK]      {fullName}  ({kind}, {assemblyName})");
        return type;
    }

    /// <summary>True when <paramref name="type"/> declares a public property with that name.</summary>
    public static bool HasProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null;

    /// <summary>
    /// Asserts that every name in <paramref name="properties"/> is a public instance property of
    /// <paramref name="type"/> and prints the full property list so a rename is visible in the report.
    /// </summary>
    public static void AssertProperties(
        Type type,
        IReadOnlyList<string> properties,
        ICollection<string> details,
        ICollection<string> failures,
        string checkId)
    {
        var missing = properties.Where(name => !HasProperty(type, name)).ToList();

        details.Add($"      properties: {string.Join(", ", type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name))}");

        if (missing.Count > 0)
        {
            failures.Add($"{checkId}: {type.Name} has no property named {string.Join(", ", missing)}.");
            details.Add($"      [FAIL] missing properties: {string.Join(", ", missing)}");
        }
    }

    /// <summary>
    /// Asserts that an enum's members are exactly <paramref name="expected"/> — no more, no less.
    /// </summary>
    /// <remarks>
    /// "Exactly" is deliberate. The five node types of spec §6.1 and the five achievement types of
    /// spec §6.2 are a closed, documented set; an extra member would mean the model had drifted
    /// from the contract the server is supposed to implement.
    /// </remarks>
    public static void AssertEnumMembers(
        Type type,
        IReadOnlyList<string> expected,
        ICollection<string> details,
        ICollection<string> failures,
        string checkId)
    {
        var actual = Enum.GetNames(type);

        details.Add($"      members   : {{{string.Join(", ", actual)}}}");
        details.Add($"      documented: {{{string.Join(", ", expected)}}}");

        var missing = expected.Where(name => !actual.Contains(name, StringComparer.Ordinal)).ToList();
        var extra = actual.Where(name => !expected.Contains(name, StringComparer.Ordinal)).ToList();

        if (missing.Count > 0 || extra.Count > 0)
        {
            failures.Add($"{checkId}: {type.Name} members are {{{string.Join(", ", actual)}}}, "
                       + $"documented set is {{{string.Join(", ", expected)}}}"
                       + (missing.Count > 0 ? $" (missing: {string.Join(", ", missing)})" : string.Empty)
                       + (extra.Count > 0 ? $" (unexpected: {string.Join(", ", extra)})" : string.Empty) + ".");
        }
    }

    /// <summary>
    /// Asserts that <paramref name="type"/> declares a method with the given name and parameter type
    /// names (compared by simple type name, so a namespace move does not break the assertion).
    /// </summary>
    public static MethodInfo? AssertMethod(
        Type type,
        string methodName,
        IReadOnlyList<string> parameterTypeNames,
        ICollection<string> details,
        ICollection<string> failures,
        string checkId,
        string? expectedReturnTypeName = null)
    {
        // Static is included on purpose: GalboxFeatureFlags.IsEnabled is the one static member of the
        // contract, and looking only at instance methods would report it as missing.
        var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name == methodName)
            .ToList();

        if (candidates.Count == 0)
        {
            details.Add($"      [FAIL] no method named {methodName}");
            failures.Add($"{checkId}: {type.Name} declares no method '{methodName}'.");
            return null;
        }

        var match = candidates.FirstOrDefault(m =>
            m.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(parameterTypeNames, StringComparer.Ordinal));

        if (match is null)
        {
            var signatures = candidates.Select(DescribeSignature).ToList();
            details.Add($"      [FAIL] {methodName} exists but with a different signature:");
            foreach (var signature in signatures)
            {
                details.Add($"             {signature}");
            }

            failures.Add($"{checkId}: {type.Name}.{methodName} has signature(s) {string.Join(" | ", signatures)}, "
                       + $"expected ({string.Join(", ", parameterTypeNames)}).");
            return null;
        }

        details.Add($"      [OK]   {DescribeSignature(match)}");

        if (expectedReturnTypeName is not null && match.ReturnType.Name != expectedReturnTypeName)
        {
            failures.Add($"{checkId}: {type.Name}.{methodName} returns {match.ReturnType.Name}, "
                       + $"expected {expectedReturnTypeName}.");
            details.Add($"      [FAIL] return type is {match.ReturnType.Name}, expected {expectedReturnTypeName}");
        }

        return match;
    }

    /// <summary>Renders a method as <c>Name(P1, P2) : Return</c> for the report.</summary>
    public static string DescribeSignature(MethodInfo method) =>
        $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))}) : {method.ReturnType.Name}";

    /// <summary>
    /// Loads one of the product's assemblies by name so it can be searched for implementations.
    /// </summary>
    /// <remarks>
    /// <c>Assembly.Load</c> rather than <c>AppDomain.CurrentDomain.GetAssemblies()</c>: a merged
    /// build only loads what it touches, and "no implementation exists" must not be reported just
    /// because the assembly that would hold one was never JIT-loaded.
    /// </remarks>
    public static Assembly? Load(string assemblyName)
    {
        try
        {
            return Assembly.Load(new AssemblyName(assemblyName));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Every type of an assembly that could actually be loaded.
    /// </summary>
    /// <remarks>
    /// <c>Galbox.App</c> cannot be fully materialised outside a WinUI host: its pages derive from
    /// <c>Microsoft.UI.Xaml.Controls.Page</c>, which is not loadable here, so <c>GetTypes()</c> throws
    /// <see cref="ReflectionTypeLoadException"/>. The exception still carries every type it <i>did</i>
    /// manage to load, and those are exactly the types these checks need - a page class that failed to
    /// load is reported as absent, never as present.
    /// </remarks>
    public static IReadOnlyList<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Select(t => t!).ToList();
        }
    }

    /// <summary>
    /// Every concrete, instantiable type in the product's own assemblies that implements
    /// <paramref name="interfaceType"/>.
    /// </summary>
    public static IReadOnlyList<Type> FindImplementations(Type interfaceType)
    {
        var found = new List<Type>();

        foreach (var name in ShippingAssemblies)
        {
            var assembly = Load(name);
            if (assembly is null)
            {
                continue;
            }

            found.AddRange(GetLoadableTypes(assembly).Where(t =>
                t is { IsClass: true, IsAbstract: false }
                && interfaceType.IsAssignableFrom(t)));
        }

        return found;
    }
}
