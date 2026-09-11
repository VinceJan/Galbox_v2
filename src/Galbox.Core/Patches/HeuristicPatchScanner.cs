namespace Galbox.Core.Patches;

/// <summary>
/// Layer 3 of the status model: third-party traces found in a game directory.
/// <para>
/// Everything this scanner produces is <b>inference</b>. It can justify "suspected" or "cannot tell",
/// and the caller must never turn it into "installed". The file exists so that the honesty rule lives in
/// code and not in a UI convention.
/// </para>
/// </summary>
public sealed class HeuristicPatchScanner
{
    private const int MaxInspectedEntries = 20_000;
    private const int MaxDepth = 4;

    /// <summary>Scans a game directory for well-known patch traces.</summary>
    public IReadOnlyList<HeuristicPatchTrace> Scan(string gameRoot, int maxTraces = 25, CancellationToken ct = default)
    {
        var traces = new List<HeuristicPatchTrace>();
        if (!Directory.Exists(LongPath.Ensure(gameRoot))) return traces;

        var inspected = 0;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((LongPath.Canonical(gameRoot), 0));

        var enginePatchSlots = new List<(string Rel, DateTime LastWrite)>();
        DateTime? dataXp3Time = null;

        while (stack.Count > 0 && traces.Count < maxTraces && inspected < MaxInspectedEntries)
        {
            ct.ThrowIfCancellationRequested();
            var (current, depth) = stack.Pop();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(LongPath.Ensure(current));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (traces.Count >= maxTraces || inspected >= MaxInspectedEntries) break;
                inspected++;

                var relative = LongPath.RelativeUnder(gameRoot, entry) ?? entry;
                var name = Path.GetFileName(entry);
                var lower = name.ToLowerInvariant();
                var isDirectory = Directory.Exists(LongPath.Ensure(entry));
                DateTime lastWrite = DateTime.MinValue;
                try
                {
                    lastWrite = isDirectory
                        ? Directory.GetLastWriteTimeUtc(LongPath.Ensure(entry))
                        : File.GetLastWriteTimeUtc(LongPath.Ensure(entry));
                }
                catch (IOException)
                {
                }

                if (isDirectory)
                {
                    if (depth < MaxDepth) stack.Push((entry, depth + 1));

                    switch (lower)
                    {
                        case "bepinex":
                            traces.Add(Trace("tool.bepinex", relative, "BepInEx plugin loader - commonly shipped with translation/injection patches.", PatchHeuristicConfidence.Medium, lastWrite));
                            continue;
                        case "autotranslator":
                        case "translation":
                            traces.Add(Trace("tool.autotranslator", relative, "XUnity.AutoTranslator style translation folder.", PatchHeuristicConfidence.Medium, lastWrite));
                            continue;
                        case "tl":
                            traces.Add(Trace("renpy.tl", relative, "Ren'Py 'tl' translation directory (a translation patch was probably applied).", PatchHeuristicConfidence.Medium, lastWrite));
                            continue;
                        case "locale emulator":
                        case "localeemulator":
                            traces.Add(Trace("tool.locale-emulator", relative, "Locale Emulator is present; this is a compatibility tool, not proof of a patch.", PatchHeuristicConfidence.Low, lastWrite));
                            continue;
                    }

                    if (lower.Contains("汉化") || lower.Contains("汉化组") || lower.Contains("patch"))
                    {
                        traces.Add(Trace("text.group-folder", relative, "Folder name mentions a localisation group or patch.", PatchHeuristicConfidence.Low, lastWrite));
                    }
                    continue;
                }

                // Files.
                if (lower.EndsWith(".xp3", StringComparison.Ordinal))
                {
                    if (lower == "data.xp3")
                    {
                        dataXp3Time = lastWrite;
                        continue;
                    }
                    if (lower.StartsWith("patch", StringComparison.Ordinal))
                    {
                        enginePatchSlots.Add((relative, lastWrite));
                        continue;
                    }
                }

                if (lower is "d3d9.dll" or "version.dll" or "winmm.dll" or "dinput8.dll" or "ddraw.dll")
                {
                    traces.Add(Trace("proxy.dll", relative, "Proxy/injection DLL in the game root (also used by compatibility wrappers).", PatchHeuristicConfidence.Low, lastWrite));
                    continue;
                }

                if (lower.Contains("xunity.autotranslator") || lower.Contains("autotranslator"))
                {
                    traces.Add(Trace("tool.autotranslator", relative, "XUnity.AutoTranslator runtime - a machine-translation patch is very likely installed.", PatchHeuristicConfidence.Medium, lastWrite));
                    continue;
                }

                if (lower.Contains("localization") && lower.EndsWith(".dll", StringComparison.Ordinal))
                {
                    traces.Add(Trace("unity.localization", relative, "Localisation assembly inside the managed folder.", PatchHeuristicConfidence.Medium, lastWrite));
                    continue;
                }

                if (lower.EndsWith(".rpyc", StringComparison.Ordinal) && relative.Contains("game", StringComparison.OrdinalIgnoreCase) && relative.Contains("tl", StringComparison.OrdinalIgnoreCase))
                {
                    traces.Add(Trace("renpy.tl-script", relative, "Compiled Ren'Py translation script.", PatchHeuristicConfidence.Medium, lastWrite));
                    continue;
                }

                var looksChineseReadme = lower is "汉化说明.txt" or "汉化说明.md" or "汉化补丁说明.txt"
                                         || (lower.Contains("汉化") && (lower.EndsWith(".txt") || lower.EndsWith(".md") || lower.EndsWith(".nfo")))
                                         || lower.Contains("readme_中文")
                                         || lower.Contains("使用说明");
                if (looksChineseReadme)
                {
                    traces.Add(Trace("text.readme", relative, "Readme text that mentions a Chinese localisation.", PatchHeuristicConfidence.Low, lastWrite));
                    continue;
                }

                if (lower.StartsWith("chinese", StringComparison.Ordinal) || lower.EndsWith(".chs", StringComparison.Ordinal) || lower.EndsWith(".cht", StringComparison.Ordinal))
                {
                    traces.Add(Trace("text.language-suffix", relative, "File name carries a Chinese language suffix.", PatchHeuristicConfidence.Low, lastWrite));
                }
            }
        }

        foreach (var (rel, when) in enginePatchSlots)
        {
            var newer = dataXp3Time is not null && when > dataXp3Time;
            traces.Add(Trace(
                "kirikiri.patch-xp3",
                rel,
                newer
                    ? "KiriKiri patch archive is newer than data.xp3, which is the usual sign of a translation patch."
                    : "KiriKiri patch archive present (its timestamp is not newer than data.xp3, so this may be original content).",
                newer ? PatchHeuristicConfidence.Medium : PatchHeuristicConfidence.Low,
                when));
        }

        return traces
            .OrderByDescending(t => t.Confidence)
            .ThenBy(t => t.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(maxTraces)
            .ToList();
    }

    private static HeuristicPatchTrace Trace(string code, string relativePath, string detail, PatchHeuristicConfidence confidence, DateTime lastWrite)
        => new()
        {
            Code = code,
            RelativePath = relativePath,
            Detail = detail,
            Confidence = confidence,
            LastWriteTime = lastWrite == DateTime.MinValue ? null : new DateTimeOffset(lastWrite, TimeSpan.Zero)
        };
}
