using System.IO;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Galbox.Data;

/// <summary>
/// Design-time factory used by the EF Core tooling (`dotnet ef migrations add`, `dotnet ef migrations script`).
/// </summary>
/// <remarks>
/// The application itself builds its <see cref="GalboxDbContext"/> through dependency injection in
/// <c>Galbox.App/App.xaml.cs</c>; this factory exists only so the CLI can construct a context without a host.
/// <para>
/// The connection string deliberately points at a scratch file / an environment-variable target instead of the
/// real user database (<c>%LocalAppData%\Galbox\galbox.db</c>), so that no design-time command can ever open,
/// lock or modify production user data. Override with the <c>GALBOX_DESIGNTIME_DB</c> environment variable when
/// a specific database is needed (for example `dotnet ef database update` against a throw-away copy).
/// </para>
/// </remarks>
public class GalboxDbContextFactory : IDesignTimeDbContextFactory<GalboxDbContext>
{
    /// <summary>
    /// Environment variable that overrides the design-time database file path.
    /// </summary>
    public const string DesignTimeDatabasePathVariable = "GALBOX_DESIGNTIME_DB";

    /// <summary>
    /// Creates a <see cref="GalboxDbContext"/> for the EF Core command line tools.
    /// </summary>
    /// <param name="args">Command line arguments forwarded by the tooling (unused).</param>
    /// <returns>A context pointing at the design-time database path.</returns>
    public GalboxDbContext CreateDbContext(string[] args)
    {
        var path = Environment.GetEnvironmentVariable(DesignTimeDatabasePathVariable);

        if (string.IsNullOrWhiteSpace(path))
        {
            // Never resolve to %LocalAppData%\Galbox\galbox.db here: design-time commands must not touch real data.
            path = Path.Combine(Path.GetTempPath(), "galbox-designtime", "galbox.db");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new DbContextOptionsBuilder<GalboxDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        return new GalboxDbContext(options);
    }
}
