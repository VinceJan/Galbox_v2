// Public entry point: turn a Ren'Py installation folder into a RenpySaveAnalysis.
//
// Usage:
//     var analyzer = new RenpySaveAnalyzer();
//     RenpySaveAnalysis analysis = analyzer.Analyze(@"D:\GAME\Dreamin'_Her");
//     foreach (var save in analysis.Saves)              // newest first
//         Console.WriteLine($"{save.SlotName} {save.CurrentSceneLabel} {save.Playtime}");
//     Console.WriteLine(analysis.CgProgress);          // 6/27 = 22.2 %
//
// Every step is optional and independently callable, so a caller that only wants the zero-risk
// ZIP metadata can use RenpySaveMetadataReader on its own.
using System.Text.RegularExpressions;
using Galbox.Core.Saves.Pickle;

namespace Galbox.Core.Saves;

/// <summary>Analyses a Ren'Py game's saves: directories, metadata, playtime, scene and CG progress.</summary>
public sealed partial class RenpySaveAnalyzer
{
    private readonly RenpySaveAnalysisOptions _options;

    /// <summary>Creates an analyzer.</summary>
    /// <param name="options">Tuning options; <see cref="RenpySaveAnalysisOptions.Default"/> when omitted.</param>
    public RenpySaveAnalyzer(RenpySaveAnalysisOptions? options = null)
        => _options = options ?? RenpySaveAnalysisOptions.Default;

    /// <summary>The options this analyzer was created with.</summary>
    public RenpySaveAnalysisOptions Options => _options;

    /// <summary>Runs the full analysis for a game installation.</summary>
    /// <param name="gameDirectory">Game root, its <c>game</c> folder, or any nested path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public RenpySaveAnalysis Analyze(string gameDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var warnings = new List<string>();

        // ---- Step 0: locate save directories ------------------------------------------
        var directories = RenpySaveLocator.Locate(gameDirectory, _options);
        warnings.AddRange(directories.Warnings);

        if (_gameVersion is null)
        {
            _gameVersion = DetectGameVersion(directories.GameRoot, directories.GameFolder, warnings);
        }

        // ---- Step 3: static script index ----------------------------------------------
        RenpyScriptIndex? scriptIndex = null;

        if (_options.BuildScriptIndex)
        {
            try
            {
                scriptIndex = RenpyScriptIndexBuilder.Build(directories.GameRoot, directories.GameFolder, warnings);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or PickleScanException)
            {
                warnings.Add($"Script index could not be built: {ex.Message}");
            }
        }

        // ---- Enumerate slots, keeping the newest mirrored copy of each -----------------
        var slots = EnumerateSlots(directories, warnings);

        // ---- Per-save analysis --------------------------------------------------------
        var results = new RenpySaveSlot[slots.Count];
        var parallelism = _options.MaxParallelism <= 0 ? Environment.ProcessorCount : _options.MaxParallelism;

        Parallel.For(0, slots.Count, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, parallelism),
            CancellationToken = cancellationToken,
        }, i => results[i] = AnalyzeSave(slots[i], scriptIndex, cancellationToken));

        // ---- Step 5: persistent + CG progress -----------------------------------------
        RenpyPersistentInfo? persistent = null;
        RenpyCgProgress? cgProgress = null;

        if (_options.ReadPersistent)
        {
            persistent = ReadPersistent(directories, scriptIndex, warnings);

            if (persistent is not null && scriptIndex is { GallerySlots.Count: > 0 })
            {
                var raw = RenpyPersistentReader.ReadFile(persistent.FilePath);
                cgProgress = RenpyPersistentReader.ComputeCgProgress(scriptIndex.GallerySlots, raw);
            }
        }

        // ---- "Unlocked scenes" — the honest replacement for a chapter percentage ------
        var unlockedScenes = Array.Empty<string>();
        var scenePercent = 0.0;

        if (persistent is not null && scriptIndex is not null && scriptIndex.LabelLines.Count > 0)
        {
            var known = scriptIndex.LabelLines.Keys.ToHashSet(StringComparer.Ordinal);

            unlockedScenes = persistent.SeenEverStringKeys
                .Where(known.Contains)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            scenePercent = unlockedScenes.Length * 100.0 / scriptIndex.LabelLines.Count;
        }

        started.Stop();

        return new RenpySaveAnalysis
        {
            Directories = directories,
            Saves = results.OrderByDescending(s => s.LastWriteTimeUtc).ToList(),
            Persistent = persistent,
            CgProgress = cgProgress,
            ScriptIndex = scriptIndex,
            Warnings = warnings,
            Elapsed = started.Elapsed,
            UnlockedSceneLabels = unlockedScenes,
            SceneProgressPercent = scenePercent,
        };
    }

    /// <summary>Analyses a single save file without touching the rest of the installation.</summary>
    /// <param name="saveFilePath">Path to a <c>.save</c> file.</param>
    /// <param name="scriptIndex">Optional static index used to resolve scene labels.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public RenpySaveSlot AnalyzeSingle(
        string saveFilePath,
        RenpyScriptIndex? scriptIndex = null,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(saveFilePath);

        if (!info.Exists)
        {
            throw new FileNotFoundException($"Save file '{saveFilePath}' does not exist.", saveFilePath);
        }

        var slot = new SlotCandidate(
            RenpySaveMetadataReader.SlotNameFromFileName(info.Name),
            info.FullName,
            RenpySaveLocationKind.Custom,
            new[] { info.FullName });

        return AnalyzeSave(slot, scriptIndex, cancellationToken);
    }

    /// <summary>Mirrors <c>MultiLocation.newest()</c>: for each slot name keep the newest copy.</summary>
    private static List<SlotCandidate> EnumerateSlots(RenpySaveDirectorySet directories, List<string> warnings)
    {
        var bySlot = new Dictionary<string, SlotCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var location in directories.Locations)
        {
            if (!location.Exists)
            {
                continue;
            }

            foreach (var fileName in location.FileNames)
            {
                var fullPath = Path.Combine(location.Path, fileName);

                if (!File.Exists(fullPath))
                {
                    continue;
                }

                var slotName = RenpySaveMetadataReader.SlotNameFromFileName(fileName);
                var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero);

                if (bySlot.TryGetValue(slotName, out var existing))
                {
                    existing.Paths.Add(fullPath);

                    if (mtime > existing.LastWriteTimeUtc)
                    {
                        existing.Path = fullPath;
                        existing.LocationKind = location.Kind;
                        existing.LastWriteTimeUtc = mtime;
                    }
                }
                else
                {
                    bySlot[slotName] = new SlotCandidate(slotName, fullPath, location.Kind, new List<string> { fullPath })
                    {
                        LastWriteTimeUtc = mtime,
                    };
                }
            }
        }

        if (bySlot.Count == 0 && directories.Success)
        {
            warnings.Add("Save directories were located but contained no readable '.save' files.");
        }

        return bySlot.Values.OrderBy(s => s.SlotName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private RenpySaveSlot AnalyzeSave(
        SlotCandidate candidate, RenpyScriptIndex? scriptIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var notes = new List<string>();
        RenpySaveMetadata? metadata = null;

        try
        {
            metadata = RenpySaveMetadataReader.Read(candidate.Path, _options.LoadScreenshots);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            notes.Add($"Metadata could not be read: {ex.Message}");
        }

        RenpyLogData? log = null;

        if (_options.ParseSaveLogs)
        {
            try
            {
                var logBytes = RenpySaveMetadataReader.ReadLogBytes(candidate.Path);

                if (logBytes is null)
                {
                    notes.Add("The save ZIP has no 'log' entry.");
                }
                else
                {
                    log = RenpyLogReader.Read(logBytes);
                }
            }
            catch (Exception ex) when (ex is PickleScanException or InvalidDataException or IOException)
            {
                notes.Add($"The 'log' pickle could not be read: {ex.Message}");
            }
        }

        var dialogue = log is null
            ? new List<RenpyDialogueEntry>()
            : BuildDialogue(log, scriptIndex);

        var allSequence = new List<string>();

        foreach (var entry in dialogue)
        {
            if (entry.SceneLabel is { Length: > 0 } label && !allSequence.Contains(label, StringComparer.Ordinal))
            {
                allSequence.Add(label);
            }
        }

        var sequence = scriptIndex is null
            ? allSequence
            : allSequence.Where(scriptIndex.StoryLabels.Contains).ToList();

        var (kind, display) = RenpyLogReader.DescribeContextCurrent(log?.ContextCurrent);
        var (sceneLabel, sceneSource) = ResolveCurrentScene(kind, log?.ContextCurrent, dialogue, scriptIndex);

        var sceneFile = sceneLabel is not null
                        && scriptIndex is not null
                        && scriptIndex.LabelFiles.TryGetValue(sceneLabel, out var file)
            ? file
            : null;

        var lastStoryLabel = sequence.Count > 0 ? sequence[^1] : null;

        var recent = dialogue.Count <= _options.RecentDialogueCount
            ? dialogue
            : dialogue.Skip(dialogue.Count - _options.RecentDialogueCount).ToList();

        var lastWithText = dialogue.LastOrDefault(d => !string.IsNullOrEmpty(d.What));

        var fileSize = metadata?.FileSize ?? (File.Exists(candidate.Path) ? new FileInfo(candidate.Path).Length : 0);
        var mtime = candidate.LastWriteTimeUtc
                    ?? new DateTimeOffset(File.GetLastWriteTimeUtc(candidate.Path), TimeSpan.Zero);

        var versionMismatch = metadata?.GameVersion is { Length: > 0 } saveVersion
                              && !string.Equals(saveVersion, _gameVersion, StringComparison.Ordinal);

        return new RenpySaveSlot
        {
            SlotName = candidate.SlotName,
            FilePath = candidate.Path,
            LocationKind = candidate.LocationKind,
            MirroredPaths = candidate.Paths,
            LastWriteTimeUtc = mtime,
            FileSize = fileSize,
            Metadata = metadata,
            PlaytimeSeconds = log?.RuntimeSeconds,
            CurrentStatement = display,
            CurrentStatementKind = kind,
            CurrentSceneLabel = sceneLabel,
            CurrentSceneLabelSource = sceneSource,
            CurrentSceneLabelFile = sceneFile,
            SceneSequence = sequence,
            AllResolvedSceneLabels = allSequence,
            LastDialogueSceneLabel = lastStoryLabel,
            Dialogue = dialogue,
            RecentDialogue = recent,
            LastDialogueWho = lastWithText?.Who,
            LastDialogueText = lastWithText?.What,
            HistoryEntryCount = log?.HistoryEntries.Count ?? 0,
            LogAudit = log?.Audit,
            IsVersionMismatch = versionMismatch,
            Notes = notes,
        };
    }

    /// <summary>Converts raw history entries into dialogue rows and resolves their scene labels.</summary>
    private static List<RenpyDialogueEntry> BuildDialogue(RenpyLogData log, RenpyScriptIndex? scriptIndex)
    {
        var results = new List<RenpyDialogueEntry>(log.HistoryEntries.Count);

        foreach (var entry in log.HistoryEntries)
        {
            var who = entry.GetString("who");
            var what = entry.GetString("what");
            var voice = entry.GetMember("voice");
            var tlid = voice?.GetPathString("tlid");
            var voiceFile = voice?.GetString("filename");

            if (who is null && what is null && tlid is null && voiceFile is null)
            {
                // History entries without any of these fields carry no scene information.
                continue;
            }

            results.Add(new RenpyDialogueEntry(
                who, what, voiceFile, tlid, ResolveLabelFromTlid(tlid, scriptIndex)));
        }

        return results;
    }

    /// <summary>
    /// Resolves a <c>tlid</c> such as <c>孤独感_28a2c752_3</c> to a script label by stripping the
    /// trailing generated suffixes one at a time and accepting the first form that is a real label.
    /// Stripping until the very end would risk matching a label that merely looks similar.
    /// </summary>
    public static string? ResolveLabelFromTlid(string? tlid, RenpyScriptIndex? scriptIndex)
    {
        if (string.IsNullOrEmpty(tlid) || scriptIndex is null || scriptIndex.LabelLines.Count == 0)
        {
            return null;
        }

        var candidate = tlid;

        while (!string.IsNullOrEmpty(candidate))
        {
            if (scriptIndex.LabelLines.ContainsKey(candidate))
            {
                return candidate;
            }

            var stripped = TlidTailRegex().Replace(candidate, string.Empty);

            if (string.Equals(stripped, candidate, StringComparison.Ordinal))
            {
                return null;
            }

            candidate = stripped;
        }

        return null;
    }

    /// <summary>
    /// Decides which scene a save sits in, preferring STORY labels.
    /// <list type="number">
    /// <item><c>Context.current</c> as a label name, when that label is a story label.</item>
    /// <item><c>Context.current</c> as a <c>from _call_*</c> name resolved through the call-site
    /// dictionary, when the result is a story label.</item>
    /// <item>The newest story label in the dialogue history (the usual case when
    /// <c>Context.current</c> is the <c>(file, timestamp, serial)</c> tuple form).</item>
    /// <item>Whatever <c>Context.current</c> resolved to, even if it is only a helper label, so a
    /// caller never gets nothing when something was in fact readable.</item>
    /// </list>
    /// The third element of the tuple form is a global statement serial, never a line number, so
    /// it contributes no position information at all.
    /// </summary>
    private static (string? Label, string? Source) ResolveCurrentScene(
        RenpyStatementKind kind,
        Pickle.PickleValue? contextCurrent,
        IReadOnlyList<RenpyDialogueEntry> dialogue,
        RenpyScriptIndex? scriptIndex)
    {
        string? directLabel = null;
        string? callSiteLabel = null;
        var directIsStory = true;
        var callSiteIsStory = true;

        if (kind == RenpyStatementKind.Label && contextCurrent?.Text is { Length: > 0 } direct)
        {
            directLabel = direct;

            // Unknown names count as story: they are demonstrably not a known helper script.
            directIsStory = scriptIndex is null
                            || !scriptIndex.LabelFiles.TryGetValue(direct, out var directFile)
                            || !RenpyScriptIndexBuilder.IsHelperScript(directFile);
        }

        if (kind == RenpyStatementKind.CallSite && contextCurrent?.Text is { Length: > 0 } callSite
            && scriptIndex is not null
            && scriptIndex.CallSiteToLabel.TryGetValue(callSite, out var enclosing))
        {
            callSiteLabel = enclosing;
            callSiteIsStory = scriptIndex.StoryLabels.Contains(enclosing);
        }

        if (directLabel is not null && directIsStory)
        {
            return (directLabel, "Context.current (label name)");
        }

        if (callSiteLabel is not null && callSiteIsStory)
        {
            return (callSiteLabel, $"Context.current '{contextCurrent!.Text}' resolved through the from-call dictionary");
        }

        for (var i = dialogue.Count - 1; i >= 0; i--)
        {
            if (dialogue[i].SceneLabel is { Length: > 0 } fromHistory
                && (scriptIndex is null || scriptIndex.StoryLabels.Contains(fromHistory)))
            {
                return (fromHistory, "newest story label in the dialogue history");
            }
        }

        if (directLabel is not null)
        {
            return (directLabel, "Context.current (helper label, no story label available)");
        }

        if (callSiteLabel is not null)
        {
            return (callSiteLabel, $"Context.current '{contextCurrent!.Text}' resolved to a helper label");
        }

        for (var i = dialogue.Count - 1; i >= 0; i--)
        {
            if (dialogue[i].SceneLabel is { Length: > 0 } fallback)
            {
                return (fallback, "newest resolved dialogue-history label");
            }
        }

        return (null, null);
    }

    private RenpyPersistentInfo? ReadPersistent(
        RenpySaveDirectorySet directories, RenpyScriptIndex? scriptIndex, List<string> warnings)
    {
        // Only the persistent(s) copied verbatim into a save directory count: games that use a
        // 'persistent' DIRECTORY instead of a file are a different Ren'Py layout we do not claim
        // to support.
        var candidate = directories.Locations
            .Where(l => l.Exists && l.HasPersistentFile)
            .Select(l => Path.Combine(l.Path, "persistent"))
            .Where(File.Exists)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (candidate is null)
        {
            warnings.Add("No 'persistent' file was found; CG and scene progress are unavailable.");
            return null;
        }

        try
        {
            var data = RenpyPersistentReader.ReadFile(candidate.FullName);
            var callSiteMap = scriptIndex?.CallSiteToLabel;

            var choices = data.Choices
                .Select(c => callSiteMap is not null && callSiteMap.TryGetValue(c.ScriptFile, out var label)
                    ? c with { SceneLabel = label }
                    : c)
                .ToList();

            return new RenpyPersistentInfo
            {
                FilePath = candidate.FullName,
                LastWriteTimeUtc = new DateTimeOffset(candidate.LastWriteTimeUtc, TimeSpan.Zero),
                RawLength = data.RawLength,
                DecompressedLength = data.DecompressedLength,
                SeenImageKeys = data.SeenImageKeys,
                SeenImageKeyCount = data.SeenImageKeyCount,
                SeenImageNames = data.SeenImageNames,
                SeenEverStringKeys = data.SeenEverStringKeys,
                SeenEverKeyCount = data.SeenEverKeyCount,
                Choices = choices,
                MovieUnlocks = data.MovieUnlocks,
                Audit = data.Audit,
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or PickleScanException or IOException)
        {
            warnings.Add($"'persistent' could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>Version of the game currently installed, read once for mismatch reporting.</summary>
    private string? _gameVersion;

    /// <summary>
    /// Reads <c>config.version</c> from the game's options.rpy (patch copy first) so saves
    /// written by an older build can be flagged. Returns <see langword="null"/> when unavailable.
    /// </summary>
    public static string? DetectGameVersion(string gameRoot, string gameFolder, List<string>? warnings = null)
    {
        const string optionsName = "options.rpy";
        var loose = Path.Combine(gameFolder, optionsName);

        if (File.Exists(loose))
        {
            var detected = MatchConfigVersion(SafeReadText(loose));

            if (detected is not null)
            {
                return detected;
            }
        }

        foreach (var archivePath in RenpySaveLocator.EnumerateArchives(gameRoot, gameFolder))
        {
            if (!File.Exists(archivePath))
            {
                continue;
            }

            try
            {
                using var archive = Rpa.RpaArchive.Open(archivePath);
                var detected = MatchConfigVersion(archive.ReadTextEntry(optionsName));

                if (detected is not null)
                {
                    return detected;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or PickleScanException)
            {
                warnings?.Add($"Could not read config.version from '{archivePath}': {ex.Message}");
            }
        }

        return null;
    }

    private static string? MatchConfigVersion(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var match = ConfigVersionRegex().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? SafeReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"config\.version\s*=\s*[""']([^""']+)[""']", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigVersionRegex();

    /// <summary>
    /// Sets the game version used to flag old saves. Read from the archives automatically when a
    /// caller does not supply it.
    /// </summary>
    public RenpySaveAnalyzer WithGameVersion(string? version)
    {
        _gameVersion = version;
        return this;
    }

    // Ren'Py generates "_<digits>" and "_<8 hex chars>" suffixes when it names call sites.
    [GeneratedRegex(@"(_\d+)?(_[0-9a-f]{8})?$", RegexOptions.CultureInvariant)]
    private static partial Regex TlidTailRegex();

    private sealed class SlotCandidate
    {
        internal SlotCandidate(
            string slotName, string path, RenpySaveLocationKind kind, IReadOnlyList<string> paths)
        {
            SlotName = slotName;
            Path = path;
            LocationKind = kind;
            Paths = paths is List<string> list ? list : paths.ToList();
        }

        internal string SlotName { get; }

        internal string Path { get; set; }

        internal RenpySaveLocationKind LocationKind { get; set; }

        internal List<string> Paths { get; }

        internal DateTimeOffset? LastWriteTimeUtc { get; set; }
    }
}
