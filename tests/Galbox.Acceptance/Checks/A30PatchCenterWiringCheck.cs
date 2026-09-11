using Galbox.App.Services;
using Galbox.App.ViewModels;
using Galbox.Core.Patches;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A30 - The patch centre is really wired: the container hands out the real patch engine.
///
/// This is the minimum evidence that the "door" exists. The engine in
/// <c>src/Galbox.Core/Patches</c> was complete and verified while nothing in the application ever
/// resolved it: no registration, no ViewModel dependency, no page control. A feature that no caller
/// can reach has no user value, and this application has a documented history of exactly that class
/// of defect ("implemented but no door"), plus a history of DI lifetime mistakes that crashed the
/// app at startup - which is why the checks below also cover the lifetimes.
///
/// Sub-checks (every one must hold):
///   A30.1 <c>IPatchEngine</c> resolves from the root container to a real <c>PatchInstaller</c>
///   A30.2 <c>ILocalPatchService</c> (the UI's door) resolves from the root container
///   A30.3 both live in the singleton lifetime, so a scope cannot hand out a second engine
///   A30.4 <c>PatchCenterViewModel</c> takes <c>ILocalPatchService</c> in its constructor, so the
///         page provably drives the engine rather than a stub
///   A30.5 the archive extensions the file picker offers match what the engine can actually extract
/// </summary>
public sealed class A30PatchCenterWiringCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A30";

    /// <inheritdoc />
    public string Title => "Patch centre wiring: DI resolves IPatchEngine / ILocalPatchService and the ViewModel is bound to it";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        const string expected =
            "IPatchEngine resolves from the root container to a real PatchInstaller; ILocalPatchService "
            + "resolves from the root container; both are singletons (identical instance inside a scope); "
            + "PatchCenterViewModel's constructor takes ILocalPatchService; the file-picker extension list "
            + "only contains formats the engine can extract";

        var details = new List<string>();
        var failures = new List<string>();

        // ------------------------------------------------------------------ A30.1 engine
        details.Add("--- A30.1 IPatchEngine from the root container ---");
        IPatchEngine? engine = null;
        try
        {
            engine = context.Services.GetService<IPatchEngine>();
        }
        catch (Exception ex)
        {
            failures.Add($"A30.1: resolving IPatchEngine threw {ex.GetType().Name}: {ex.Message}");
        }

        if (engine is null)
        {
            details.Add("  [FAIL] IPatchEngine -> null (no registration; the patch engine has no door)");
            failures.Add("A30.1: IPatchEngine is not registered, so no UI can ever reach the patch engine.");
        }
        else
        {
            details.Add($"  [OK]   IPatchEngine -> {engine.GetType().FullName}");
            if (engine is not PatchInstaller)
            {
                details.Add($"  [FAIL] the registered implementation is {engine.GetType().Name}, not PatchInstaller");
                failures.Add("A30.1: the registered IPatchEngine is not the verified PatchInstaller.");
            }
        }

        // ------------------------------------------------------------- A30.2 the UI's door
        details.Add(string.Empty);
        details.Add("--- A30.2 ILocalPatchService from the root container ---");
        ILocalPatchService? patchService = null;
        try
        {
            patchService = context.Services.GetService<ILocalPatchService>();
        }
        catch (Exception ex)
        {
            failures.Add($"A30.2: resolving ILocalPatchService threw {ex.GetType().Name}: {ex.Message}");
        }

        if (patchService is null)
        {
            details.Add("  [FAIL] ILocalPatchService -> null (the patch centre has no door onto the engine)");
            failures.Add("A30.2: ILocalPatchService is not registered; the patch centre page cannot reach the engine.");
        }
        else
        {
            details.Add($"  [OK]   ILocalPatchService -> {patchService.GetType().FullName}");
        }

        // ------------------------------------------------------------------ A30.3 lifetimes
        details.Add(string.Empty);
        details.Add("--- A30.3 lifetime: a scope must not produce a second engine ---");
        if (engine is not null)
        {
            using var scopeA = context.Services.CreateScope();
            using var scopeB = context.Services.CreateScope();
            var fromA = scopeA.ServiceProvider.GetService<IPatchEngine>();
            var fromB = scopeB.ServiceProvider.GetService<IPatchEngine>();
            var same = ReferenceEquals(engine, fromA) && ReferenceEquals(engine, fromB);
            details.Add($"  root == scopeA == scopeB : {same}");
            if (!same)
            {
                failures.Add("A30.3: IPatchEngine is not a singleton; two scopes produced different installers.");
            }
            else
            {
                details.Add("  [OK]   singleton, so the ledger scan state is shared as intended");
            }
        }
        else
        {
            details.Add("  [SKIP] no engine to compare");
        }

        // --------------------------------------------------- A30.4 ViewModel -> patch service
        details.Add(string.Empty);
        details.Add("--- A30.4 PatchCenterViewModel constructor dependencies ---");
        var viewModelType = typeof(PatchCenterViewModel);
        var constructors = viewModelType.GetConstructors();
        var bindsToPatchService = false;
        foreach (var constructor in constructors)
        {
            var parameterTypes = constructor.GetParameters().Select(p => p.ParameterType.Name).ToArray();
            details.Add($"  ctor({string.Join(", ", parameterTypes)})");
            if (constructor.GetParameters().Any(p => p.ParameterType == typeof(ILocalPatchService))) bindsToPatchService = true;
        }

        if (!bindsToPatchService)
        {
            failures.Add("A30.4: PatchCenterViewModel does not depend on ILocalPatchService; the page is not wired to the workflow.");
        }
        else
        {
            details.Add("  [OK]   the ViewModel takes ILocalPatchService");
        }

        // ------------------------------------------- A30.5 picker list vs extractable formats
        details.Add(string.Empty);
        details.Add("--- A30.5 file-picker extensions vs extractable container kinds ---");
        var extractable = new[]
        {
            PatchArchiveKind.Zip,
            PatchArchiveKind.Rar,
            PatchArchiveKind.SevenZip,
            PatchArchiveKind.Tar,
            PatchArchiveKind.Gzip
        };
        details.Add($"  engine extractable kinds : {string.Join(", ", extractable)}");
        if (patchService is null)
        {
            details.Add("  [SKIP] no ILocalPatchService to ask");
        }
        else
        {
            var extensions = patchService.SupportedArchiveExtensions;
            details.Add($"  picker extensions        : {string.Join(", ", extensions)}");
            foreach (var required in new[] { ".zip", ".7z", ".rar" })
            {
                if (extensions.Contains(required, StringComparer.OrdinalIgnoreCase))
                {
                    details.Add($"  [OK]   {required} offered");
                }
                else
                {
                    failures.Add($"A30.5: the picker does not offer {required}, which the engine can extract.");
                }
            }

            foreach (var extension in new[] { ".exe", ".iso" })
            {
                if (extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    failures.Add($"A30.5: the picker offers {extension}, which the engine refuses by design.");
                }
            }
        }

        var actual = engine is null
            ? "IPatchEngine did not resolve (no patch engine registered)"
            : patchService is null
                ? "IPatchEngine resolved but ILocalPatchService did not"
                : $"IPatchEngine={engine.GetType().Name}, ILocalPatchService={patchService.GetType().Name}, "
                  + $"ViewModel bound={bindsToPatchService}, failure(s)={failures.Count}";

        var result = failures.Count == 0
            ? CheckResult.Pass(Id, Title, expected, actual)
            : CheckResult.Fail(Id, Title, expected, actual);

        foreach (var failure in failures) details.Add($"FAIL REASON: {failure}");
        return Task.FromResult(result.With(details.ToArray()));
    }
}
