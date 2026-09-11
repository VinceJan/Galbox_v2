using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galbox.App.Services;

/// <summary>One repair that was applied and can still be undone.</summary>
internal sealed class HealthFixJournalEntry
{
    /// <summary>"RenameInstallPath" or "WindowsCompatibility".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Game the repair was applied to.</summary>
    public int GameId { get; set; }

    /// <summary>Install path before a rename.</summary>
    public string? OldPath { get; set; }

    /// <summary>Install path after a rename.</summary>
    public string? NewPath { get; set; }

    /// <summary>Executable a compatibility layer was written for.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Compatibility layer value that was written.</summary>
    public string? AppliedValue { get; set; }

    /// <summary>Value that was replaced, or null when there was none.</summary>
    public string? PreviousValue { get; set; }

    /// <summary>UTC time of the repair.</summary>
    public DateTime AppliedTimeUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Durable record of the repairs this application applied, so that "撤销" still works after a restart.
/// </summary>
/// <remarks>
/// The alternative - keeping the previous values only in memory - would mean that closing the
/// application turns an undoable change into a permanent one, and the user would have no way to learn
/// what was changed. The file lives next to the other application data and holds nothing but the
/// values needed to reverse a repair.
///
/// Every method is best-effort: a failed journal write never fails the repair itself (the repair has
/// already happened by then) but is reported back to the caller as a warning string.
/// </remarks>
internal sealed class GameHealthFixJournal
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();

    /// <summary>Creates a journal inside <paramref name="directory"/>, creating it when needed.</summary>
    public GameHealthFixJournal(string directory)
    {
        Directory = directory;
        FilePath = Path.Combine(directory, "applied-fixes.json");
    }

    /// <summary>Directory holding the journal file.</summary>
    public string Directory { get; }

    /// <summary>Full path of the journal file.</summary>
    public string FilePath { get; }

    /// <summary>Reads every entry. Returns an empty list when the file is missing or unreadable.</summary>
    public List<HealthFixJournalEntry> Read()
    {
        lock (_gate)
        {
            return ReadUnlocked();
        }
    }

    /// <summary>Returns the most recent entry matching <paramref name="predicate"/>.</summary>
    public HealthFixJournalEntry? FindLatest(Func<HealthFixJournalEntry, bool> predicate)
    {
        lock (_gate)
        {
            return ReadUnlocked()
                .Where(predicate)
                .OrderByDescending(entry => entry.AppliedTimeUtc)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Adds or replaces the entry that describes the current state of one repair: the newest
    /// application of the same kind to the same game wins, so an undo always reverses the last action.
    /// </summary>
    public string? Record(HealthFixJournalEntry entry)
    {
        lock (_gate)
        {
            var entries = ReadUnlocked();

            entries.RemoveAll(existing =>
                string.Equals(existing.Kind, entry.Kind, StringComparison.Ordinal)
                && existing.GameId == entry.GameId);

            entries.Add(entry);
            return WriteUnlocked(entries);
        }
    }

    /// <summary>Removes the entry matching <paramref name="predicate"/>.</summary>
    public string? Remove(Func<HealthFixJournalEntry, bool> predicate)
    {
        lock (_gate)
        {
            var entries = ReadUnlocked();
            var removed = entries.RemoveAll(existing => predicate(existing));
            return removed == 0 ? null : WriteUnlocked(entries);
        }
    }

    private List<HealthFixJournalEntry> ReadUnlocked()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new List<HealthFixJournalEntry>();
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<HealthFixJournalEntry>>(json, SerializerOptions)
                ?? new List<HealthFixJournalEntry>();
        }
        catch
        {
            // A corrupt journal must not break the diagnosis page; an empty one only means that
            // "撤销" falls back to its conservative path.
            return new List<HealthFixJournalEntry>();
        }
    }

    private string? WriteUnlocked(List<HealthFixJournalEntry> entries)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(entries, SerializerOptions));
            return null;
        }
        catch (Exception ex)
        {
            return $"撤销记录写入失败（{ex.Message}），本次修复仍然生效，但重启后可能无法一键撤销。";
        }
    }
}
