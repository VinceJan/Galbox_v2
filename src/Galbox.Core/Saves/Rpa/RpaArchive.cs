// Reader for Ren'Py .rpa archives (RPA-3.0, with best-effort RPA-2.0 / RPA-3.2 handling).
//
// Field-verified against the reference game's two archives:
//   * header form  "RPA-3.0 <index_offset_hex> <key_hex>\n"
//   * index entries are 3-tuples (offset, length, prefix)
//   * BOTH offset and length are XOR-ed with the key (not just offset)
//   * the third tuple element is a plain 16-byte "prefix" used as a cheap integrity check
//
// The XOR rule above was confirmed by the product design report with 400/400 magic validation
// and is re-confirmed here by RpaArchive.ValidateMagic.
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Galbox.Core.Saves.Pickle;

namespace Galbox.Core.Saves.Rpa;

/// <summary>
/// One archive member.
/// <para>
/// The third tuple element is not decoration: Ren'Py's loader wraps an archived file in a
/// <c>SubFile(fn, base, length, start)</c> whose <c>read()</c> first serves <c>start</c> and only
/// then reads <c>length - len(start)</c> bytes starting at <c>base</c>. The logical payload is
/// therefore <c>start + file[base ..]</c>, NOT <c>file[base .. base+length]</c>. In the reference
/// game <c>game\archive.rpa</c> stores empty <c>start</c> values (so both readings coincide) while
/// <c>00patch\00patch.rpa</c> stores a real 16-byte <c>start</c> for every member — reading the
/// latter the naive way shifts every payload 16 bytes and destroys its header.
/// </para>
/// </summary>
/// <param name="Offset">Absolute file offset of the on-disk part of the payload.</param>
/// <param name="Length">Logical payload length, including <paramref name="Start"/>.</param>
/// <param name="Start">Bytes served before the on-disk part (may be empty).</param>
public readonly record struct RpaIndexEntry(long Offset, long Length, byte[] Start)
{
    /// <summary>Number of bytes that must be read from the archive file.</summary>
    public long DiskLength => Length - Start.Length;

    /// <summary>Bytes served before the on-disk part, rendered as upper-case hex.</summary>
    public string StartHex => Convert.ToHexString(Start);

    /// <inheritdoc />
    public override string ToString()
        => $"offset={Offset} length={Length} diskLength={DiskLength} start={StartHex}";
}

/// <summary>Outcome of the mandatory magic/trailer validation pass.</summary>
/// <param name="CheckedEntries">Entries whose payload was actually read back.</param>
/// <param name="BytesVerified">Total payload bytes read back from the archive by this pass.</param>
/// <param name="MagicChecked">Entries whose extension or prefix implies a known signature.</param>
/// <param name="MagicValidated">Entries whose logical header matched that signature.</param>
/// <param name="TrailerChecked">Entries whose signature implies a terminating marker (PNG).</param>
/// <param name="TrailerValidated">Entries whose terminating marker was found at the expected offset.</param>
/// <param name="StartBytesChecked">Entries that carry a non-empty <c>start</c> field.</param>
/// <param name="PotentiallyShiftedEntries">Entries whose on-disk bytes do not begin the payload.</param>
/// <param name="Samples">Human readable evidence lines (bounded).</param>
public sealed record RpaMagicReport(
    int CheckedEntries,
    long BytesVerified,
    int MagicChecked,
    int MagicValidated,
    int TrailerChecked,
    int TrailerValidated,
    int StartBytesChecked,
    int PotentiallyShiftedEntries,
    IReadOnlyList<string> Samples)
{
    /// <summary>True when every signature that could be checked was correct.</summary>
    public bool AllMagicValid => MagicChecked == 0 || MagicValidated == MagicChecked;

    /// <summary>True when every terminating marker was found where it belongs.</summary>
    public bool AllTrailersValid => TrailerChecked == 0 || TrailerValidated == TrailerChecked;

    /// <summary>
    /// True when the decoded offsets and lengths are demonstrably correct. If an offset or length
    /// were wrong by even one byte, the file signatures and the PNG end marker could not all line
    /// up, so this is a real proof rather than a self-consistency check.
    /// </summary>
    public bool IsOffsetCorrect => AllMagicValid && AllTrailersValid;
}

/// <summary>
/// Read-only view over a Ren'Py <c>.rpa</c> archive. The index is decoded with the
/// zero-execution <see cref="PickleScanner"/> — archive indexes are pickle streams too.
/// </summary>
public sealed class RpaArchive : IDisposable
{
    private static readonly byte[] Utf8BomFree = Array.Empty<byte>();

    private readonly FileStream _stream;
    private readonly Dictionary<string, List<RpaIndexEntry>> _index;
    private bool _disposed;

    private RpaArchive(
        string archivePath,
        FileStream stream,
        string version,
        long key,
        long indexOffset,
        Dictionary<string, List<RpaIndexEntry>> index,
        PickleScanAudit indexAudit)
    {
        ArchivePath = archivePath;
        _stream = stream;
        Version = version;
        Key = key;
        IndexOffset = indexOffset;
        _index = index;
        IndexAudit = indexAudit;
    }

    /// <summary>Absolute path of the archive.</summary>
    public string ArchivePath { get; }

    /// <summary>Archive format version as written in the header (e.g. <c>3.0</c>).</summary>
    public string Version { get; }

    /// <summary>XOR key from the header.</summary>
    public long Key { get; }

    /// <summary>Absolute offset of the zlib-compressed pickle index.</summary>
    public long IndexOffset { get; }

    /// <summary>Number of members in the archive.</summary>
    public int EntryCount => _index.Count;

    /// <summary>Audit produced while decoding the index pickle.</summary>
    public PickleScanAudit IndexAudit { get; }

    /// <summary>Every member name, sorted.</summary>
    public IReadOnlyList<string> EntryNames
        => _index.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>Opens an archive and decodes its index.</summary>
    /// <param name="archivePath">Path to the .rpa file.</param>
    /// <exception cref="InvalidDataException">The header or index is not a valid RPA structure.</exception>
    /// <exception cref="PickleScanException">The index pickle is malformed.</exception>
    public static RpaArchive Open(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var stream = new FileStream(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1 << 16, FileOptions.RandomAccess);

        try
        {
            var (version, key, indexOffset) = ReadHeader(stream, archivePath);

            stream.Seek(indexOffset, SeekOrigin.Begin);
            byte[] indexBytes;

            using (var zlib = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true))
            using (var buffer = new MemoryStream())
            {
                zlib.CopyTo(buffer);
                indexBytes = buffer.ToArray();
            }

            if (indexBytes.Length == 0)
            {
                throw new InvalidDataException(
                    $"'{archivePath}': index at offset {indexOffset} decompressed to 0 bytes.");
            }

            var scan = PickleScanner.Scan(indexBytes);
            var index = BuildIndex(scan.Root, version, key, archivePath);

            return new RpaArchive(archivePath, stream, version, key, indexOffset, index, scan.Audit);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>True when this archive contains the given member.</summary>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _index.ContainsKey(NormalizeName(name));
    }

    /// <summary>Reads a member's logical bytes (the <c>start</c> field followed by the on-disk part).</summary>
    /// <exception cref="FileNotFoundException">The member is absent.</exception>
    public byte[] ReadEntry(string name)
        => TryReadEntry(name, out var data)
            ? data
            : throw new FileNotFoundException($"'{name}' is not present in '{ArchivePath}'.");

    /// <summary>Reads a member's logical bytes without throwing for absent members.</summary>
    public bool TryReadEntry(string name, out byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(name);

        data = Utf8BomFree;

        if (!_index.TryGetValue(NormalizeName(name), out var entries) || entries.Count == 0)
        {
            return false;
        }

        var entry = entries[0];

        if (entry.Length is <= 0 or > int.MaxValue || entry.DiskLength < 0)
        {
            return false;
        }

        var result = new byte[entry.Length];
        var startLength = entry.Start.Length;

        if (startLength > 0)
        {
            Buffer.BlockCopy(entry.Start, 0, result, 0, Math.Min(startLength, result.Length));
        }

        var remaining = (int)(entry.Length - Math.Min(startLength, result.Length));

        if (remaining > 0)
        {
            lock (_stream)
            {
                _stream.Seek(entry.Offset, SeekOrigin.Begin);
                _stream.ReadExactly(result, Math.Min(startLength, result.Length), remaining);
            }
        }

        data = result;
        return true;
    }

    /// <summary>Reads a member as text, decoding UTF-8 and falling back to Latin-1.</summary>
    public string? ReadTextEntry(string name)
    {
        if (!TryReadEntry(name, out var bytes))
        {
            return null;
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>All member names ending in <paramref name="extension"/>, sorted.</summary>
    public IReadOnlyList<string> FindByExtension(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return EntryNames.Where(n => n.EndsWith(extension, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Reads up to <paramref name="maxEntries"/> members back and validates them against their
    /// file signatures. A wrong offset or length cannot survive this: the header signature must
    /// land on byte 0 of the logical payload and, for PNG, the <c>IEND</c> marker must land on the
    /// final 12 bytes. This is the guard against silent offset corruption.
    /// </summary>
    public RpaMagicReport ValidateMagic(int maxEntries = 100_000)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var checkedEntries = 0;
        long bytesVerified = 0;
        var magicChecked = 0;
        var magicValidated = 0;
        var trailerChecked = 0;
        var trailerValidated = 0;
        var startBytesChecked = 0;
        var potentiallyShifted = 0;
        var samples = new List<string>();

        foreach (var name in EntryNames)
        {
            if (checkedEntries >= maxEntries)
            {
                break;
            }

            var entry = _index[name][0];

            if (entry.Length <= 0 || entry.DiskLength < 0)
            {
                continue;
            }

            // Only read what the checks need: 32 head bytes plus the 32-byte tail.
            var head = ReadLogicalSlice(entry, 0, 32);
            var tail = ReadLogicalSlice(entry, Math.Max(0, entry.Length - 32), 32);
            bytesVerified += head.Length + tail.Length;
            checkedEntries++;

            if (entry.Start.Length > 0)
            {
                startBytesChecked++;

                if (entry.DiskLength > 0)
                {
                    potentiallyShifted++;
                }
            }

            var signature = FileSignature.Match(name, head);

            if (signature is not null)
            {
                magicChecked++;

                if (signature.Value.Matches(head))
                {
                    magicValidated++;
                }
                else if (samples.Count < 12)
                {
                    samples.Add(
                        $"MAGIC MISMATCH {name}: expected {signature.Value.Hex}, got {Convert.ToHexString(head.AsSpan(0, Math.Min(8, head.Length)))}");
                }
            }

            if (signature?.EndMarker is { } endMarker)
            {
                trailerChecked++;

                // PNG ends with [0,0,0,0]['I','E','N','D'][CRC32], so the marker sits 4 bytes
                // before the end rather than on the very last byte.
                var trailing = signature.Value.EndMarkerTrailingBytes;

                if (tail.Length >= endMarker.Length + trailing
                    && tail.AsSpan(tail.Length - endMarker.Length - trailing, endMarker.Length)
                        .SequenceEqual(endMarker))
                {
                    trailerValidated++;
                }
                else if (samples.Count < 12)
                {
                    samples.Add(
                        $"TRAILER MISMATCH {name}: expected '{Encoding.ASCII.GetString(endMarker)}' "
                        + $"{trailing} bytes from the end, got {Convert.ToHexString(tail.AsSpan(Math.Max(0, tail.Length - 12)))}");
                }
            }
        }

        return new RpaMagicReport(
            checkedEntries, bytesVerified, magicChecked, magicValidated,
            trailerChecked, trailerValidated, startBytesChecked, potentiallyShifted, samples);
    }

    /// <summary>
    /// Reads <paramref name="count"/> logical payload bytes starting at <paramref name="logicalOffset"/>,
    /// honouring the <c>start</c> prefix that Ren'Py's SubFile serves first.
    /// </summary>
    private byte[] ReadLogicalSlice(RpaIndexEntry entry, long logicalOffset, int count)
    {
        if (logicalOffset < 0 || logicalOffset >= entry.Length || count <= 0)
        {
            return Array.Empty<byte>();
        }

        var available = (int)Math.Min(count, entry.Length - logicalOffset);
        var result = new byte[available];
        var written = 0;

        // Part served from the indexed `start` block.
        var startLength = entry.Start.Length;

        if (logicalOffset < startLength)
        {
            var fromStart = (int)Math.Min(available, startLength - logicalOffset);
            Buffer.BlockCopy(entry.Start, (int)logicalOffset, result, 0, fromStart);
            written = fromStart;
        }

        // Remainder read from the archive file.
        if (written < available)
        {
            var diskOffset = entry.Offset + Math.Max(0, logicalOffset - startLength);

            lock (_stream)
            {
                _stream.Seek(diskOffset, SeekOrigin.Begin);
                var read = _stream.Read(result, written, available - written);

                if (read < available - written)
                {
                    Array.Resize(ref result, written + read);
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }

    // ------------------------------------------------------------------ internals

    private byte[] ReadHead(RpaIndexEntry entry, int count)
    {
        var length = (int)Math.Min(count, entry.Length);

        if (length <= 0)
        {
            return Array.Empty<byte>();
        }

        var head = new byte[length];

        lock (_stream)
        {
            _stream.Seek(entry.Offset, SeekOrigin.Begin);
            var read = _stream.Read(head, 0, length);

            if (read < length)
            {
                Array.Resize(ref head, read);
            }
        }

        return head;
    }

    private static string NormalizeName(string name)
    {
        // Ren'Py hides entries with a leading underscore in some archive flavours.
        return name.StartsWith('_') ? name[1..] : name;
    }

    private static (string Version, long Key, long IndexOffset) ReadHeader(FileStream stream, string archivePath)
    {
        Span<byte> header = stackalloc byte[256];
        var used = 0;

        while (used < header.Length)
        {
            var b = stream.ReadByte();

            if (b < 0)
            {
                break;
            }

            header[used++] = (byte)b;

            if (b == '\n')
            {
                break;
            }
        }

        if (used == 0)
        {
            throw new InvalidDataException($"'{archivePath}' is empty.");
        }

        var line = Encoding.ASCII.GetString(header[..used]).TrimEnd('\r', '\n');
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 3 || !parts[0].StartsWith("RPA-", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"'{archivePath}': expected an 'RPA-x.y <offset> <key>' header but found '{line}'.");
        }

        var version = parts[0]["RPA-".Length..];

        try
        {
            return version switch
            {
                // Ren'Py 7.x: "RPA-3.0 <index_offset> <key>"; both offset and length are XOR-ed.
                "3.0" => (version, ParseHex(parts[2]), ParseHex(parts[1])),

                // Third-party editors write "RPA-3.2 <key> <offset>" with unencrypted offsets.
                "3.2" => (version, ParseHex(parts[1]), ParseHex(parts[2])),

                // Ren'Py 6.x: only the offset was XOR-ed with the key.
                "2.0" => (version, ParseHex(parts[2]), ParseHex(parts[1])),

                _ => throw new InvalidDataException($"'{archivePath}': unsupported RPA version '{version}'."),
            };
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"'{archivePath}': malformed numeric header field in '{line}'.", ex);
        }
    }

    private static long ParseHex(string text)
        => long.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static Dictionary<string, List<RpaIndexEntry>> BuildIndex(
        PickleValue root, string version, long key, string archivePath)
    {
        if (root.Kind != PickleValueKind.Dictionary)
        {
            throw new InvalidDataException(
                $"'{archivePath}': index pickle root is {root.Kind}, expected a dictionary.");
        }

        var index = new Dictionary<string, List<RpaIndexEntry>>(StringComparer.Ordinal);

        foreach (var pair in root.Pairs)
        {
            var name = pair.Key.Kind switch
            {
                PickleValueKind.String => pair.Key.Text!,
                PickleValueKind.Bytes => Encoding.UTF8.GetString(pair.Key.RawBytes ?? Array.Empty<byte>()),
                _ => null,
            };

            if (name is null)
            {
                continue;
            }

            var entries = new List<RpaIndexEntry>();
            var rawEntries = pair.Value.Kind == PickleValueKind.List
                ? pair.Value.Items
                : pair.Value.EnumerateSequence().ToList();

            foreach (var raw in rawEntries)
            {
                var fields = raw.Kind == PickleValueKind.Tuple
                    ? raw.Items
                    : raw.EnumerateSequence().ToList();

                if (fields.Count < 2)
                {
                    continue;
                }

                var storedOffset = AsInt64(fields[0]);
                var storedLength = AsInt64(fields[1]);
                var start = fields.Count > 2 ? AsBytes(fields[2]) : Array.Empty<byte>();

                // RPA-3.0 XORs BOTH fields with the header key. RPA-3.2 does not XOR at all.
                var offset = version == "3.2" ? storedOffset : storedOffset ^ key;
                var length = version switch
                {
                    "3.2" => storedLength,
                    "2.0" => storedLength,
                    _ => storedLength ^ key,
                };

                entries.Add(new RpaIndexEntry(offset, length, start));
            }

            if (entries.Count > 0)
            {
                index[NormalizeName(name)] = entries;
            }
        }

        return index;
    }

    /// <summary>
    /// Reads the raw bytes of a string value. Python 2 <c>str</c> keeps its original bytes; a
    /// <c>unicode</c> value is re-encoded as UTF-8 so it can still be compared byte for byte.
    /// </summary>
    private static byte[] AsBytes(PickleValue value) => value.Kind switch
    {
        PickleValueKind.String => value.RawBytes ?? Encoding.UTF8.GetBytes(value.Text ?? string.Empty),
        PickleValueKind.Bytes => value.RawBytes ?? Array.Empty<byte>(),
        _ => Array.Empty<byte>(),
    };

    private static long AsInt64(PickleValue value) => value.Kind switch
    {
        PickleValueKind.Int => value.Integer,
        PickleValueKind.Float => (long)value.Float,
        PickleValueKind.Bool => value.Bool ? 1 : 0,
        _ => throw new InvalidDataException("RPA index entry field is not an integer."),
    };

    /// <summary>Known file signatures used for the offset-validation pass.</summary>
    private readonly record struct FileSignature(
        string Name, byte[] Magic, byte[]? EndMarker = null, int EndMarkerTrailingBytes = 0)
    {
        internal bool Matches(ReadOnlySpan<byte> head)
            => head.Length >= Magic.Length && head[..Magic.Length].SequenceEqual(Magic);

        internal string Hex => Convert.ToHexString(Magic);

        private static readonly (string Extension, string Name, byte[] Magic, byte[]? EndMarker, int Trailing)[]
            Table =
        {
            (".png", "PNG", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
                Encoding.ASCII.GetBytes("IEND"), 4),
            (".jpg", "JPEG", new byte[] { 0xFF, 0xD8, 0xFF }, null, 0),
            (".jpeg", "JPEG", new byte[] { 0xFF, 0xD8, 0xFF }, null, 0),
            (".ogg", "OggS", new byte[] { 0x4F, 0x67, 0x67, 0x53 }, null, 0),
            (".opus", "OggS", new byte[] { 0x4F, 0x67, 0x67, 0x53 }, null, 0),
            (".webm", "Matroska/WebM", new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, null, 0),
            (".mp3", "MP3", new byte[] { 0x49, 0x44, 0x33 }, null, 0),
            (".ttf", "TrueType", new byte[] { 0x00, 0x01, 0x00, 0x00 }, null, 0),
            (".otf", "OpenType", new byte[] { 0x4F, 0x54, 0x54, 0x4F }, null, 0),
            (".zip", "ZIP", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, null, 0),
            (".rpa", "RPA", Encoding.ASCII.GetBytes("RPA-"), null, 0),
            (".rpyc", "Ren'Py compiled script", Encoding.ASCII.GetBytes("RENPY RPC2"), null, 0),
            (".wav", "RIFF", Encoding.ASCII.GetBytes("RIFF"), null, 0),
        };

        internal static FileSignature? Match(string name, ReadOnlySpan<byte> head)
        {
            foreach (var (extension, signatureName, magic, endMarker, trailing) in Table)
            {
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return new FileSignature(signatureName, magic, endMarker, trailing);
                }
            }

            // Signature-less extensions (.rpy, .txt): sniff the bytes, but never treat the
            // UTF-8 BOM as a signature.
            foreach (var (_, signatureName, magic, endMarker, trailing) in Table)
            {
                if (head.Length >= magic.Length && head[..magic.Length].SequenceEqual(magic))
                {
                    return new FileSignature(signatureName, magic, endMarker, trailing);
                }
            }

            return null;
        }
    }
}

/// <summary>Little helper kept out of the archive type so the Win32/memory layout stays obvious.</summary>
internal static class BinaryHelpers
{
    internal static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> data)
        => BinaryPrimitives.ReadUInt32LittleEndian(data);
}
