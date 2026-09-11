namespace Galbox.App.Services;

/// <summary>
/// Single place that answers "where does Galbox keep its per-user data".
///
/// The shipping application stores everything under <c>%LocalAppData%\Galbox</c>, and
/// <c>GALBOX_DATA_DIR</c> redirects that whole folder to a chosen directory. The application
/// entry point already honours the variable for the SQLite database
/// (<c>App.GetAppDataPath</c>), and this helper is the same rule expressed once, so the
/// neighbouring stores - the startup log and the save-backup store - cannot drift away from it.
/// </summary>
/// <remarks>
/// Unset (the normal case) the result is exactly <c>%LocalAppData%\Galbox</c> and nothing changes.
/// Set, every store that asks this helper lands inside the override - which is what makes it
/// possible to run the shipping executable against a THROWAWAY data directory. A verification run
/// can then read the startup log of the process it launched instead of sharing one day-stamped
/// file with every other Galbox instance on the machine, and it can never touch the real library.
///
/// The override is honoured leniently on purpose: diagnostics must never be able to break the code
/// path they observe, so an override that cannot be turned into a directory falls back to the
/// default location instead of throwing.
/// </remarks>
public static class GalboxDataDirectory
{
    /// <summary>Environment variable that redirects the whole per-user data folder.</summary>
    public const string DataDirectoryVariable = "GALBOX_DATA_DIR";

    /// <summary>Folder name of the startup logs, relative to the data folder.</summary>
    public const string LogFolderName = "logs";

    /// <summary>Folder name of the save-backup store, relative to the data folder.</summary>
    public const string SaveBackupFolderName = "SaveBackups";

    /// <summary>File-name prefix of the day-stamped startup log.</summary>
    public const string StartupLogFilePrefix = "startup-";

    /// <summary>
    /// The per-user data folder: <c>GALBOX_DATA_DIR</c> when it is set and usable, otherwise
    /// <c>%LocalAppData%\Galbox</c>.
    /// </summary>
    public static string Resolve() => Resolve(Environment.GetEnvironmentVariable(DataDirectoryVariable));

    /// <summary>
    /// Resolves a candidate override without reading the environment, so a caller that launches a
    /// child process with an explicit <c>GALBOX_DATA_DIR</c> can compute the very same folder the
    /// child will use.
    /// </summary>
    /// <param name="dataDirectoryOverride">The value the child will see, or null/blank for none.</param>
    public static string Resolve(string? dataDirectoryOverride)
    {
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox");

        if (string.IsNullOrWhiteSpace(dataDirectoryOverride))
        {
            return defaultPath;
        }

        try
        {
            return Path.GetFullPath(dataDirectoryOverride);
        }
        catch (Exception)
        {
            // An unusable override must not break startup; behave as if it were unset.
            return defaultPath;
        }
    }

    /// <summary>
    /// The startup-log folder for this process: <c>{data folder}\logs</c>, honouring
    /// <c>GALBOX_DATA_DIR</c>.
    /// </summary>
    public static string ResolveLogDirectory() => Path.Combine(Resolve(), LogFolderName);

    /// <summary>
    /// The startup-log folder for an explicit data-folder value. Pass the exact string the child
    /// process will see in <c>GALBOX_DATA_DIR</c>; null means "the default data folder".
    /// </summary>
    public static string ResolveLogDirectory(string? dataDirectoryOverride)
        => Path.Combine(Resolve(dataDirectoryOverride), LogFolderName);

    /// <summary>
    /// Path of the startup log of one day: <c>{data folder}\logs\startup-YYYYMMDD.log</c>.
    /// </summary>
    /// <param name="dataDirectoryOverride">Data folder override, or null for the default.</param>
    /// <param name="timestamp">Moment the day stamp is taken from; null means now.</param>
    public static string ResolveStartupLogPath(string? dataDirectoryOverride = null, DateTimeOffset? timestamp = null)
    {
        var day = (timestamp ?? DateTimeOffset.Now).ToString("yyyyMMdd");
        return Path.Combine(ResolveLogDirectory(dataDirectoryOverride), $"{StartupLogFilePrefix}{day}.log");
    }

    /// <summary>The save-backup store: <c>{data folder}\SaveBackups</c>.</summary>
    public static string ResolveSaveBackupDirectory()
        => Path.Combine(Resolve(), SaveBackupFolderName);
}
