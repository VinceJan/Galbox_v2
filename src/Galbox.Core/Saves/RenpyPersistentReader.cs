// Step 5 — the shared "persistent" file.
//
// Unlike .save (a ZIP), 'persistent' is a BARE ZLIB STREAM (magic 0x78 0x5E) whose payload is a
// protocol-2 pickle of renpy.persistent.Persistent. The fields we need live in that object's
// BUILD state: _seen_images, _seen_ever, _chosen, opmv/kakoed/granded.
//
// IMPORTANT (measured, contradicts an earlier reading of the design report):
// _seen_images keys are NOT plain strings. Ren'Py's renpy.seen_image(name) tokenises the name
// with tuple(name.split()), so every key is a TUPLE. A single-token image such as the gallery
// slot "0101" is stored as ('0101',). Comparing the declared slot string directly against the
// key set therefore yields an intersection of ZERO; the slot must be tokenised first.
// Of the 283 keys in the reference save, 66 are single-element tuples (27 of which are CG
// gallery slots) and 217 are multi-element character-sprite composites.
using System.IO.Compression;
using Galbox.Core.Saves.Pickle;

namespace Galbox.Core.Saves;

/// <summary>Raw contents of the <c>persistent</c> file.</summary>
public sealed record RenpyPersistentData
{
    /// <summary>Raw key count of <c>_seen_images</c>.</summary>
    public int SeenImageKeyCount { get; init; }

    /// <summary>All <c>_seen_images</c> keys, rendered for display.</summary>
    public IReadOnlyList<string> SeenImageKeys { get; init; } = Array.Empty<string>();

    /// <summary>Single-element tuple keys of <c>_seen_images</c>, i.e. real image names.</summary>
    public IReadOnlyList<string> SeenImageNames { get; init; } = Array.Empty<string>();

    /// <summary><c>_seen_images</c> kept as scanned values, for exact set membership tests.</summary>
    public IReadOnlyList<PickleValue> SeenImageKeyValues { get; init; } = Array.Empty<PickleValue>();

    /// <summary>Raw key count of <c>_seen_ever</c>.</summary>
    public int SeenEverKeyCount { get; init; }

    /// <summary>String keys of <c>_seen_ever</c> (labels and <c>_call_*</c> automatics).</summary>
    public IReadOnlyList<string> SeenEverStringKeys { get; init; } = Array.Empty<string>();

    /// <summary>Statement-tuple keys of <c>_seen_ever</c>.</summary>
    public int SeenEverStatementKeyCount { get; init; }

    /// <summary>Entries of <c>_chosen</c>.</summary>
    public IReadOnlyList<RenpyChoiceRecord> Choices { get; init; } = Array.Empty<RenpyChoiceRecord>();

    /// <summary>Movie gallery unlock flags.</summary>
    public IReadOnlyDictionary<string, bool> MovieUnlocks { get; init; }
        = new Dictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>Top-level attribute names found on the Persistent object.</summary>
    public IReadOnlyList<string> TopLevelKeys { get; init; } = Array.Empty<string>();

    /// <summary>Size of the raw compressed file.</summary>
    public int RawLength { get; init; }

    /// <summary>Size after decompression.</summary>
    public int DecompressedLength { get; init; }

    /// <summary>Audit of the pickle scan.</summary>
    public required PickleScanAudit Audit { get; init; }
}

/// <summary>Reads Ren'Py <c>persistent</c> files.</summary>
public static class RenpyPersistentReader
{
    /// <summary>The three movie-gallery flags the reference game exposes.</summary>
    public static readonly string[] MovieFlagNames = { "opmv", "kakoed", "granded" };

    /// <summary>Reads and parses a persistent file from disk.</summary>
    /// <exception cref="InvalidDataException">The file is not a zlib stream or pickle.</exception>
    public static RenpyPersistentData ReadFile(string persistentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(persistentPath);

        var raw = File.ReadAllBytes(persistentPath);
        return Read(raw);
    }

    /// <summary>Reads and parses raw persistent bytes.</summary>
    public static RenpyPersistentData Read(byte[] rawBytes)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);

        var body = Decompress(rawBytes);
        var scan = PickleScanner.Scan(body);
        var persistent = scan.Root;

        var seenImages = persistent.GetMember("_seen_images");
        var seenImageKeys = new List<string>();
        var seenImageNames = new List<string>();
        var seenImageValues = new List<PickleValue>();

        foreach (var pair in seenImages?.EnumeratePairs() ?? Enumerable.Empty<KeyValuePair<PickleValue, PickleValue>>())
        {
            var key = pair.Key;
            seenImageValues.Add(key);

            if (key.Kind == PickleValueKind.Tuple)
            {
                var tokens = key.Items
                    .Where(i => i.Kind == PickleValueKind.String)
                    .Select(i => i.Text ?? string.Empty)
                    .ToArray();

                seenImageKeys.Add("(" + string.Join(", ", tokens) + ")");

                if (tokens.Length == 1)
                {
                    seenImageNames.Add(tokens[0]);
                }
            }
            else if (key.Kind == PickleValueKind.String)
            {
                seenImageKeys.Add(key.Text ?? string.Empty);
                seenImageNames.Add(key.Text ?? string.Empty);
            }
        }

        seenImageNames.Sort(StringComparer.Ordinal);
        seenImageKeys.Sort(StringComparer.Ordinal);

        var seenEver = persistent.GetMember("_seen_ever");
        var seenEverStrings = new List<string>();
        var seenEverStatements = 0;

        foreach (var pair in seenEver?.EnumeratePairs() ?? Enumerable.Empty<KeyValuePair<PickleValue, PickleValue>>())
        {
            switch (pair.Key.Kind)
            {
                case PickleValueKind.String:
                    seenEverStrings.Add(pair.Key.Text ?? string.Empty);
                    break;
                case PickleValueKind.Tuple:
                    seenEverStatements++;
                    break;
            }
        }

        seenEverStrings.Sort(StringComparer.Ordinal);

        var chosen = persistent.GetMember("_chosen");
        var choices = new List<RenpyChoiceRecord>();

        foreach (var pair in chosen?.EnumeratePairs() ?? Enumerable.Empty<KeyValuePair<PickleValue, PickleValue>>())
        {
            // Key shape: ((script_file, compile_timestamp, statement_serial), choice_text)
            if (pair.Key.Kind != PickleValueKind.Tuple || pair.Key.Items.Count < 2)
            {
                continue;
            }

            var identity = pair.Key.Items[0];
            var text = pair.Key.Items[1].Kind == PickleValueKind.String ? pair.Key.Items[1].Text : null;

            if (text is null)
            {
                continue;
            }

            var file = identity.Kind == PickleValueKind.Tuple && identity.Items.Count > 0
                       && identity.Items[0].Kind == PickleValueKind.String
                ? identity.Items[0].Text ?? "?"
                : "?";

            var timestamp = identity.Kind == PickleValueKind.Tuple && identity.Items.Count > 1
                            && identity.Items[1].Kind == PickleValueKind.Int
                ? identity.Items[1].Integer
                : 0;

            var serial = identity.Kind == PickleValueKind.Tuple && identity.Items.Count > 2
                         && identity.Items[2].Kind == PickleValueKind.Int
                ? identity.Items[2].Integer
                : 0;

            choices.Add(new RenpyChoiceRecord(file, timestamp, serial, text, null));
        }

        var movies = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var name in MovieFlagNames)
        {
            var flag = persistent.GetMember(name);
            movies[name] = flag is { Kind: PickleValueKind.Bool } && flag.Bool;
        }

        var topLevel = persistent
            .EnumeratePairs()
            .Where(p => p.Key.Kind == PickleValueKind.String)
            .Select(p => p.Key.Text ?? string.Empty)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        return new RenpyPersistentData
        {
            SeenImageKeyCount = seenImageValues.Count,
            SeenImageKeys = seenImageKeys,
            SeenImageNames = seenImageNames,
            SeenImageKeyValues = seenImageValues,
            SeenEverKeyCount = seenEverStrings.Count + seenEverStatements,
            SeenEverStringKeys = seenEverStrings,
            SeenEverStatementKeyCount = seenEverStatements,
            Choices = choices,
            MovieUnlocks = movies,
            TopLevelKeys = topLevel,
            RawLength = rawBytes.Length,
            DecompressedLength = body.Length,
            Audit = scan.Audit,
        };
    }

    /// <summary>
    /// Decompresses a persistent payload. Ren'Py 7.x writes a bare zlib stream; a few builds
    /// wrap it in a ZIP, so both containers are accepted.
    /// </summary>
    public static byte[] Decompress(byte[] rawBytes)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);

        if (rawBytes.Length == 0)
        {
            throw new InvalidDataException("persistent file is empty.");
        }

        // ZIP container (magic "PK\x03\x04").
        if (rawBytes.Length > 4
            && rawBytes[0] == 0x50 && rawBytes[1] == 0x4B && rawBytes[2] == 0x03 && rawBytes[3] == 0x04)
        {
            using var zipStream = new MemoryStream(rawBytes, writable: false);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
            var entry = zip.GetEntry("log") ?? zip.Entries.FirstOrDefault()
                ?? throw new InvalidDataException("persistent ZIP container has no entries.");

            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);
            return buffer.ToArray();
        }

        // Bare zlib stream (magic 0x78 followed by a valid check byte).
        if (rawBytes[0] == 0x78)
        {
            try
            {
                using var input = new MemoryStream(rawBytes, writable: false);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                return output.ToArray();
            }
            catch (InvalidDataException)
            {
                // Fall through and report a precise error below.
            }
        }

        throw new InvalidDataException(
            $"persistent payload is neither a zlib stream nor a ZIP container (first bytes: "
            + Convert.ToHexString(rawBytes.AsSpan(0, Math.Min(8, rawBytes.Length))) + ").");
    }

    /// <summary>
    /// Computes CG gallery completion by intersecting the slots declared in the game's
    /// <c>gallery.rpy</c> with <c>persistent._seen_images</c>.
    /// </summary>
    /// <param name="declaredSlots">Slot ids from <c>gallery.image("...")</c>, in declaration order.</param>
    /// <param name="persistent">Parsed persistent data.</param>
    public static RenpyCgProgress ComputeCgProgress(
        IReadOnlyList<string> declaredSlots, RenpyPersistentData persistent)
    {
        ArgumentNullException.ThrowIfNull(declaredSlots);
        ArgumentNullException.ThrowIfNull(persistent);

        // Exact membership test: tokenise the slot the way renpy.seen_image does.
        var keySet = new HashSet<PickleValue>(persistent.SeenImageKeyValues, PickleValueStructuralComparer.Instance);

        var unlocked = new List<string>();
        var locked = new List<string>();

        foreach (var slot in declaredSlots)
        {
            var key = ImageKeyFor(slot);

            if (keySet.Contains(key))
            {
                unlocked.Add(slot);
            }
            else
            {
                locked.Add(slot);
            }
        }

        unlocked.Sort(StringComparer.Ordinal);

        return new RenpyCgProgress
        {
            TotalCount = declaredSlots.Count,
            UnlockedCount = unlocked.Count,
            Percent = declaredSlots.Count == 0 ? 0 : unlocked.Count * 100.0 / declaredSlots.Count,
            UnlockedSlots = unlocked,
            LockedSlots = locked,
            DeclaredSlots = declaredSlots,
            SeenImageKeyCount = persistent.SeenImageKeyCount,
        };
    }

    /// <summary>
    /// Builds the <c>_seen_images</c> key for an image name, mirroring
    /// <c>renpy.exports.seen_image</c>: <c>tuple(name.split())</c>.
    /// </summary>
    public static PickleValue ImageKeyFor(string imageName)
    {
        var tokens = imageName.Split(
            new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        return PickleValue.MakeTuple(
            Array.ConvertAll(tokens, t => PickleValue.MakeString(t, null)));
    }
}
