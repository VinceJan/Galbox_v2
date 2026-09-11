using Galbox.Core.Patches;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Core;

/// <summary>Dependency-injection entry point for the local patch installer.</summary>
public static class PatchesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the patch installer engine. The engine is stateless apart from the files it writes,
    /// so a singleton is correct and keeps the ledger scans cheap.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="options">Optional configuration; conservative defaults apply when omitted.</param>
    public static IServiceCollection AddGalboxPatches(this IServiceCollection services, PatchInstallerOptions? options = null)
    {
        var effective = options ?? new PatchInstallerOptions();
        services.AddSingleton(effective);
        services.AddSingleton<PatchLedger>(_ => new PatchLedger(effective));
        services.AddSingleton<PatchBackupStore>(_ => new PatchBackupStore(effective));
        services.AddSingleton<PatchStatusEvaluator>(_ => new PatchStatusEvaluator(effective, new PatchLedger(effective)));
        services.AddSingleton<IPatchEngine>(_ => new PatchInstaller(effective));
        return services;
    }
}
