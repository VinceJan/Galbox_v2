using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Galbox.Core.Patches;

/// <summary>Low level reader used by the inspector and the sandbox extractor.</summary>
internal interface IPatchEntrySource : IDisposable
{
    PatchArchiveKind Kind { get; }

    string FormatId { get; }

    bool IsEncrypted { get; }

    IReadOnlyList<PatchArchiveEntry> Entries { get; }

    Stream OpenEntry(PatchArchiveEntry entry);
}

/// <summary>Outcome of opening a container.</summary>
internal sealed class PatchEntrySourceResult
{
    public required IPatchEntrySource Source { get; init; }

    public required PatchNameEncoding EffectiveEncoding { get; init; }

    public bool AutoDetected { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Opens patch containers. ZIP goes through the BCL (<c>System.IO.Compression</c>);
/// RAR/7z/tar/gz go through SharpCompress.
/// </summary>
internal static class PatchArchiveReaderFactory
{
    public static PatchArchiveKind DetectKind(string path)
    {
        var header = ReadHeader(path, 512);
        if (header.Length >= 2 && header[0] == (byte)'M' && header[1] == (byte)'Z')
        {
            return PatchArchiveKind.SelfExtractingExecutable;
        }

        if (header.Length >= 4 && header[0] == 0x50 && header[1] == 0x4B &&
            (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07))
        {
            return PatchArchiveKind.Zip;
        }

        if (header.Length >= 7 && header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21 &&
            header[4] == 0x1A && header[5] == 0x07)
        {
            return PatchArchiveKind.Rar;
        }

        if (header.Length >= 6 && header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC &&
            header[3] == 0xAF && header[4] == 0x27 && header[5] == 0x1C)
        {
            return PatchArchiveKind.SevenZip;
        }

        if (header.Length >= 2 && header[0] == 0x1F && header[1] == 0x8B)
        {
            return PatchArchiveKind.Gzip;
        }

        if (header.Length >= 262 && Encoding.ASCII.GetString(header, 257, 5) == "ustar")
        {
            return PatchArchiveKind.Tar;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".iso" or ".mdf" or ".mds" or ".nrg" or ".bin" or ".cue" => PatchArchiveKind.DiscImage,
            ".exe" or ".msi" => PatchArchiveKind.SelfExtractingExecutable,
            _ => PatchArchiveKind.Unknown
        };
    }

    public static string FormatIdOf(PatchArchiveKind kind) => kind switch
    {
        PatchArchiveKind.Zip => "zip",
        PatchArchiveKind.Rar => "rar",
        PatchArchiveKind.SevenZip => "7z",
        PatchArchiveKind.Tar => "tar",
        PatchArchiveKind.Gzip => "tar.gz",
        PatchArchiveKind.SelfExtractingExecutable => "sfx-exe",
        PatchArchiveKind.DiscImage => "disc-image",
        _ => "unknown"
    };

    public static PatchEntrySourceResult Open(string path, PatchArchiveReadOptions options)
    {
        PatchNameEncodings.EnsureRegistered();
        var kind = DetectKind(path);

        return kind switch
        {
            PatchArchiveKind.Zip => OpenZip(path, options),
            PatchArchiveKind.Rar or PatchArchiveKind.SevenZip or PatchArchiveKind.Tar or PatchArchiveKind.Gzip
                => OpenSharpCompress(path, kind, options),
            _ => throw new PatchRejectedException(
                kind == PatchArchiveKind.SelfExtractingExecutable ? PatchRejectionCode.SelfExtractingExecutable : PatchRejectionCode.UnsupportedFormat,
                kind == PatchArchiveKind.SelfExtractingExecutable
                    ? "This is a self-extracting executable (SFX). Galbox never executes or auto-extracts installers."
                    : $"Unsupported or unrecognised archive format ({kind}).",
                kind == PatchArchiveKind.SelfExtractingExecutable
                    ? "Run it manually inside an isolated directory, or unpack it with 7-Zip yourself and then drop the resulting archive into Galbox."
                    : "Repack the patch as a .zip, .7z or .rar archive and try again.")
        };
    }

    private static PatchEntrySourceResult OpenZip(string path, PatchArchiveReadOptions options)
    {
        var notes = new List<string>();
        var chosen = options.NameEncoding;
        var autoDetected = false;

        var centralDirectory = ZipCentralDirectoryReader.TryRead(path, out var cdNote);
        if (cdNote is not null) notes.Add(cdNote);

        if (chosen == PatchNameEncoding.Auto)
        {
            autoDetected = true;
            chosen = options.ForceNameEncoding ? PatchNameEncoding.Cp932 : PatchNameEncoding.Utf8;

            if (centralDirectory is { Count: > 0 })
            {
                // Probe with the raw central-directory bytes instead of relying on the BCL decoder:
                // System.IO.Compression applies entryNameEncoding to every entry and ignores the
                // per-entry UTF-8 flag, so a global choice can only be validated this way.
                foreach (var candidate in PatchNameEncodings.AutoFallbackOrder)
                {
                    var encoding = PatchNameEncodings.Resolve(candidate);
                    var bad = 0;
                    foreach (var entry in centralDirectory)
                    {
                        if (entry.IsDirectory && entry.RawName.Length == 0) continue;
                        if (entry.Utf8Flag && candidate != PatchNameEncoding.Utf8) continue;
                        if (PatchNameEncodings.LooksMisdecoded(encoding.GetString(entry.RawName))) bad++;
                    }

                    if (bad == 0)
                    {
                        chosen = candidate;
                        break;
                    }
                    notes.Add($"Zip name decoding probe: {PatchNameEncodings.ToId(candidate)} produced {bad} undecodable name(s).");
                }

                notes.Add($"Zip entry names carry the UTF-8 flag on {centralDirectory.Count(e => e.Utf8Flag)}/{centralDirectory.Count} entries; the flag is treated as a hint, not as proof.");
            }
            else
            {
                chosen = PatchNameEncoding.Cp932;
                notes.Add("Central directory could not be parsed; name encoding falls back to CP932 with a UTF-8 probe.");
            }
            notes.Add($"Name encoding auto-detected as {PatchNameEncodings.ToId(chosen)}. Override it manually if file names look garbled.");
        }

        var enc = PatchNameEncodings.Resolve(chosen);
        var stream = new FileStream(LongPath.Ensure(path), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        try
        {
            var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: enc);
            var entries = new List<PatchArchiveEntry>(archive.Entries.Count);

            for (var index = 0; index < archive.Entries.Count; index++)
            {
                var entry = archive.Entries[index];
                string name = entry.FullName;
                var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || entry.Name.Length == 0;

                if (centralDirectory is not null && index < centralDirectory.Count)
                {
                    var cdEntry = centralDirectory[index];
                    // Trust the per-entry UTF-8 flag when it is set: that is what the ZIP spec says and
                    // it keeps genuinely UTF-8 archives readable even when the fallback is CP932.
                    name = cdEntry.Utf8Flag
                        ? Encoding.UTF8.GetString(cdEntry.RawName)
                        : enc.GetString(cdEntry.RawName);
                }

                var mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
                var isLink = (mode & 0xF000) == 0xA000;

                entries.Add(new PatchArchiveEntry
                {
                    Index = index,
                    Name = name,
                    IsDirectory = isDirectory,
                    SizeBytes = entry.Length,
                    IsLink = isLink
                });
            }

            if (centralDirectory is not null && centralDirectory.Count != entries.Count)
            {
                notes.Add($"Central directory lists {centralDirectory.Count} entries but the BCL reader produced {entries.Count}; the BCL names were used for the difference.");
            }

            return new PatchEntrySourceResult
            {
                Source = new ZipPatchEntrySource(path, stream, archive, entries),
                EffectiveEncoding = chosen,
                AutoDetected = autoDetected,
                Notes = notes
            };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static PatchEntrySourceResult OpenSharpCompress(string path, PatchArchiveKind kind, PatchArchiveReadOptions options)
    {
        var notes = new List<string>();
        var chosen = options.NameEncoding == PatchNameEncoding.Auto ? PatchNameEncoding.Cp932 : options.NameEncoding;
        var autoDetected = options.NameEncoding == PatchNameEncoding.Auto;

        var archiveEncoding = new SharpCompress.Common.ArchiveEncoding
        {
            Default = PatchNameEncodings.Resolve(chosen)
        };
        if (options.ForceNameEncoding)
        {
            archiveEncoding.Forced = PatchNameEncodings.Resolve(chosen);
        }

        var readerOptions = new ReaderOptions { ArchiveEncoding = archiveEncoding, LeaveStreamOpen = false };

        // SharpCompress maps a .tar.gz to a GZipArchive holding one *unnamed* entry, which cannot be
        // installed (and would be rejected by the path guard). Decompress once, then decide whether the
        // payload is a tar (the normal case) or a single compressed file.
        if (kind == PatchArchiveKind.Gzip)
        {
            return OpenGzipPayload(path, readerOptions, chosen, autoDetected, notes);
        }

        var stream = new FileStream(LongPath.Ensure(path), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);

        try
        {
            var archive = ArchiveFactory.Open(stream, readerOptions);
            var entries = new List<PatchArchiveEntry>();
            var index = 0;

            foreach (var entry in archive.Entries)
            {
                entries.Add(new PatchArchiveEntry
                {
                    Index = index++,
                    Name = entry.Key ?? string.Empty,
                    IsDirectory = entry.IsDirectory,
                    SizeBytes = entry.Size,
                    IsLink = !string.IsNullOrEmpty(entry.LinkTarget)
                });
            }

            if (autoDetected)
            {
                notes.Add($"Archive names were decoded with {PatchNameEncodings.ToId(chosen)} (non-UTF-8 entries); override manually if they look garbled.");
            }

            return new PatchEntrySourceResult
            {
                Source = new SharpCompressPatchEntrySource(kind, FormatIdOf(kind), stream, archive, entries),
                EffectiveEncoding = chosen,
                AutoDetected = autoDetected,
                Notes = notes
            };
        }
        catch (SharpCompress.Common.CryptographicException ex)
        {
            stream.Dispose();
            throw new PatchRejectedException(PatchRejectionCode.EncryptedArchive, $"The archive is password protected: {ex.Message}", "Extract it manually and re-pack the contents without a password.");
        }
        catch (Exception ex) when (ex is not PatchRejectedException)
        {
            stream.Dispose();
            throw new PatchRejectedException(PatchRejectionCode.CorruptArchive, $"The archive could not be read: {ex.Message}");
        }
    }

    private static PatchEntrySourceResult OpenGzipPayload(
        string path,
        ReaderOptions readerOptions,
        PatchNameEncoding chosen,
        bool autoDetected,
        List<string> notes)
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "Galbox", "patchtmp");
        Directory.CreateDirectory(LongPath.Ensure(tempFolder));
        var tempPath = Path.Combine(tempFolder, Guid.NewGuid().ToString("N") + ".inflated");
        var cleanupOnFailure = true;

        try
        {
            using (var input = new FileStream(LongPath.Ensure(path), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new FileStream(LongPath.Ensure(tempPath), FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            {
                gzip.CopyTo(output);
            }

            var inflated = new FileInfo(LongPath.Ensure(tempPath)).Length;

            if (LooksLikeTar(tempPath))
            {
                var stream = new FileStream(LongPath.Ensure(tempPath), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
                var archive = TarArchive.Open(stream, readerOptions);
                var entries = new List<PatchArchiveEntry>();
                var index = 0;
                foreach (var entry in archive.Entries)
                {
                    entries.Add(new PatchArchiveEntry
                    {
                        Index = index++,
                        Name = entry.Key ?? string.Empty,
                        IsDirectory = entry.IsDirectory,
                        SizeBytes = entry.Size,
                        IsLink = !string.IsNullOrEmpty(entry.LinkTarget)
                    });
                }

                notes.Add($"gzip payload ({inflated} bytes) was inflated to a temporary file and read as tar.");
                cleanupOnFailure = false;
                return new PatchEntrySourceResult
                {
                    Source = new SharpCompressPatchEntrySource(PatchArchiveKind.Tar, "tar.gz", stream, archive, entries, tempPath),
                    EffectiveEncoding = chosen,
                    AutoDetected = autoDetected,
                    Notes = notes
                };
            }

            // A plain .gz holds exactly one file whose name is not stored inside the container,
            // so the name is taken from the file name, the way gzip itself does.
            var baseName = Path.GetFileName(path);
            var syntheticName = baseName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(baseName)
                : baseName + ".out";
            if (string.IsNullOrEmpty(syntheticName)) syntheticName = "payload.bin";

            notes.Add($"The .gz payload is not a tar; it is exposed as a single file named '{syntheticName}' ({inflated} bytes).");
            cleanupOnFailure = false;
            return new PatchEntrySourceResult
            {
                Source = new SingleFilePatchEntrySource(tempPath, syntheticName, inflated),
                EffectiveEncoding = chosen,
                AutoDetected = autoDetected,
                Notes = notes
            };
        }
        catch (InvalidDataException ex)
        {
            throw new PatchRejectedException(PatchRejectionCode.CorruptArchive, $"The gzip stream could not be decompressed: {ex.Message}");
        }
        finally
        {
            if (cleanupOnFailure)
            {
                try { if (File.Exists(LongPath.Ensure(tempPath))) File.Delete(LongPath.Ensure(tempPath)); }
                catch (IOException) { }
            }
        }
    }

    private static bool LooksLikeTar(string path)
    {
        try
        {
            using var stream = new FileStream(LongPath.Ensure(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[512];
            var read = 0;
            while (read < buffer.Length)
            {
                var chunk = stream.Read(buffer, read, buffer.Length - read);
                if (chunk <= 0) return false;
                read += chunk;
            }
            return Encoding.ASCII.GetString(buffer, 257, 5) == "ustar";
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static byte[] ReadHeader(string path, int length)
    {
        try
        {
            using var stream = new FileStream(LongPath.Ensure(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min(length, Math.Max(16, (int)Math.Min(stream.Length, length)))];
            var read = 0;
            while (read < buffer.Length)
            {
                var chunk = stream.Read(buffer, read, buffer.Length - read);
                if (chunk <= 0) break;
                read += chunk;
            }
            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch (IOException)
        {
            return Array.Empty<byte>();
        }
    }

    private sealed class ZipPatchEntrySource : IPatchEntrySource
    {
        private readonly FileStream _stream;
        private readonly ZipArchive _archive;

        public ZipPatchEntrySource(string path, FileStream stream, ZipArchive archive, IReadOnlyList<PatchArchiveEntry> entries)
        {
            SourcePath = path;
            _stream = stream;
            _archive = archive;
            Entries = entries;
        }

        public string SourcePath { get; }

        public PatchArchiveKind Kind => PatchArchiveKind.Zip;

        public string FormatId => "zip";

        public bool IsEncrypted => false;

        public IReadOnlyList<PatchArchiveEntry> Entries { get; }

        public Stream OpenEntry(PatchArchiveEntry entry) => _archive.Entries[entry.Index].Open();

        public void Dispose()
        {
            _archive.Dispose();
            _stream.Dispose();
        }
    }

    private sealed class SharpCompressPatchEntrySource : IPatchEntrySource
    {
        private readonly FileStream _stream;
        private readonly IArchive _archive;
        private readonly List<SharpCompress.Archives.IArchiveEntry> _entries;
        private readonly string? _tempFile;

        public SharpCompressPatchEntrySource(
            PatchArchiveKind kind,
            string formatId,
            FileStream stream,
            IArchive archive,
            IReadOnlyList<PatchArchiveEntry> entries,
            string? tempFile = null)
        {
            Kind = kind;
            FormatId = formatId;
            _stream = stream;
            _archive = archive;
            _tempFile = tempFile;
            Entries = entries;
            _entries = archive.Entries.ToList();
            IsEncrypted = _entries.Any(e => e.IsEncrypted);
        }

        public PatchArchiveKind Kind { get; }

        public string FormatId { get; }

        public bool IsEncrypted { get; }

        public IReadOnlyList<PatchArchiveEntry> Entries { get; }

        public Stream OpenEntry(PatchArchiveEntry entry) => _entries[entry.Index].OpenEntryStream();

        public void Dispose()
        {
            try
            {
                _archive.Dispose();
                _stream.Dispose();
            }
            finally
            {
                if (_tempFile is not null)
                {
                    try { if (File.Exists(LongPath.Ensure(_tempFile))) File.Delete(LongPath.Ensure(_tempFile)); }
                    catch (IOException) { }
                }
            }
        }
    }

    /// <summary>
    /// Exposes the payload of a plain <c>.gz</c> as one file. The container stores no file name inside,
    /// so the name is derived from the archive file name exactly like the gzip tool does.
    /// </summary>
    private sealed class SingleFilePatchEntrySource : IPatchEntrySource
    {
        private readonly string _tempFile;

        public SingleFilePatchEntrySource(string tempFile, string name, long size)
        {
            _tempFile = tempFile;
            Entries = new[]
            {
                new PatchArchiveEntry { Index = 0, Name = name, IsDirectory = false, SizeBytes = size }
            };
        }

        public PatchArchiveKind Kind => PatchArchiveKind.Gzip;

        public string FormatId => "gz";

        public bool IsEncrypted => false;

        public IReadOnlyList<PatchArchiveEntry> Entries { get; }

        public Stream OpenEntry(PatchArchiveEntry entry) => new FileStream(LongPath.Ensure(_tempFile), FileMode.Open, FileAccess.Read, FileShare.Read);

        public void Dispose()
        {
            try { if (File.Exists(LongPath.Ensure(_tempFile))) File.Delete(LongPath.Ensure(_tempFile)); }
            catch (IOException) { }
        }
    }
}

/// <summary>
/// Minimal central-directory parser used only to get <b>raw</b> entry name bytes and the per-entry
/// UTF-8 flag. <c>System.IO.Compression</c> applies a single encoding to every entry and ignores the
/// flag, which is unusable for mixed / mis-flagged galgame archives.
/// </summary>
internal static class ZipCentralDirectoryReader
{
    internal sealed record Entry(byte[] RawName, bool Utf8Flag, bool IsDirectory);

    public static List<Entry>? TryRead(string path, out string? note)
    {
        note = null;
        try
        {
            using var stream = new FileStream(LongPath.Ensure(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 22) return null;

            var tailLength = (int)Math.Min(stream.Length, 66_000);
            var tail = new byte[tailLength];
            stream.Seek(stream.Length - tailLength, SeekOrigin.Begin);
            ReadExactly(stream, tail);

            var eocd = LastIndexOf(tail, new byte[] { 0x50, 0x4B, 0x05, 0x06 });
            if (eocd < 0)
            {
                note = "End of central directory record not found; entry names fall back to the BCL decoder.";
                return null;
            }

            long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16, 4));
            long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12, 4));
            long count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10, 2));

            // ZIP64 locator sits immediately before the classic EOCD.
            var absoluteEocd = stream.Length - tailLength + eocd;
            if (absoluteEocd >= 20)
            {
                var locator = new byte[20];
                stream.Seek(absoluteEocd - 20, SeekOrigin.Begin);
                ReadExactly(stream, locator);
                if (locator[0] == 0x50 && locator[1] == 0x4B && locator[2] == 0x06 && locator[3] == 0x07)
                {
                    var zip64Offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8, 8));
                    var zip64 = new byte[56];
                    stream.Seek(zip64Offset, SeekOrigin.Begin);
                    ReadExactly(stream, zip64);
                    if (zip64[0] == 0x50 && zip64[1] == 0x4B && zip64[2] == 0x06 && zip64[3] == 0x06)
                    {
                        count = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32, 8));
                        cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40, 8));
                        cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48, 8));
                    }
                }
            }

            if (count <= 0 || cdSize <= 0 || cdOffset <= 0 || cdOffset + cdSize > stream.Length) return null;

            var cd = new byte[cdSize];
            stream.Seek(cdOffset, SeekOrigin.Begin);
            ReadExactly(stream, cd);

            var result = new List<Entry>((int)Math.Min(count, 1_000_000));
            var cursor = 0;
            while (cursor + 46 <= cd.Length)
            {
                if (cd[cursor] != 0x50 || cd[cursor + 1] != 0x4B || cd[cursor + 2] != 0x01 || cd[cursor + 3] != 0x02) break;

                var flags = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(cursor + 8, 2));
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(cursor + 28, 2));
                var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(cursor + 30, 2));
                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(cursor + 32, 2));

                if (cursor + 46 + nameLength > cd.Length) break;
                var rawName = cd.AsSpan(cursor + 46, nameLength).ToArray();
                var isDirectory = nameLength > 0 && (rawName[^1] == (byte)'/' || rawName[^1] == (byte)'\\');
                result.Add(new Entry(rawName, (flags & 0x0800) != 0, isDirectory));

                cursor += 46 + nameLength + extraLength + commentLength;
            }

            if (result.Count == 0)
            {
                note = "Central directory contained no parsable entries.";
                return null;
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            note = $"Central directory parse failed ({ex.GetType().Name}); entry names fall back to the BCL decoder.";
            return null;
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk <= 0) throw new EndOfStreamException();
            read += chunk;
        }
    }

    private static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }
}
