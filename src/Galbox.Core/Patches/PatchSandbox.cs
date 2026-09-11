namespace Galbox.Core.Patches;

/// <summary>One file that was successfully unpacked into the sandbox.</summary>
public sealed class ExtractedArchiveFile
{
    /// <summary>Index inside the archive.</summary>
    public int ArchiveEntryIndex { get; init; }

    /// <summary>Decoded entry name as it appeared in the archive.</summary>
    public required string ArchiveEntryName { get; init; }

    /// <summary>Path relative to the sandbox files root (already normalised/sanitised).</summary>
    public required string RelativePath { get; init; }

    /// <summary>Absolute path inside the sandbox.</summary>
    public required string SandboxFullPath { get; init; }

    /// <summary>Bytes written.</summary>
    public long SizeBytes { get; init; }

    /// <summary>SHA-256 of the extracted bytes.</summary>
    public required string Sha256 { get; init; }

    /// <summary>True when the archive name had to be normalised to be representable on Windows.</summary>
    public bool NameSanitized { get; init; }

    /// <summary>What was changed.</summary>
    public string? SanitizationNote { get; init; }
}

/// <summary>Result of unpacking an archive into the sandbox.</summary>
public sealed class PatchSandboxResult
{
    /// <summary>Root of this sandbox (unique per preview).</summary>
    public required string SandboxRoot { get; init; }

    /// <summary>Directory that mirrors the eventual landing layout.</summary>
    public required string FilesRoot { get; init; }

    /// <summary>Extracted files, in archive order.</summary>
    public required IReadOnlyList<ExtractedArchiveFile> Files { get; init; }

    /// <summary>Entries that were refused, with reasons.</summary>
    public IReadOnlyList<RejectedArchiveEntry> Rejected { get; init; } = Array.Empty<RejectedArchiveEntry>();

    /// <summary>Archive identity.</summary>
    public required PatchArchiveInfo Archive { get; init; }

    /// <summary>Total bytes written into the sandbox.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Non-fatal observations.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Two-phase extraction. Nothing is ever written into the game directory from here: the archive is
/// fully unpacked into a throwaway sandbox first, so a mid-way failure cannot leave the game half patched.
/// </summary>
public static class PatchSandbox
{
    /// <summary>Creates a unique sandbox path for an install attempt.</summary>
    public static string NewSandboxPath(string sandboxRoot, string installId)
        => Path.Combine(sandboxRoot, installId);

    /// <summary>
    /// Unpacks <paramref name="archivePath"/> into a fresh sandbox.
    /// Zip Slip attempts are rejected and recorded; they never reach the disk.
    /// </summary>
    public static async Task<PatchSandboxResult> ExtractAsync(
        string archivePath,
        string sandboxRoot,
        PatchInstallerOptions options,
        PatchArchiveReadOptions? readOptions = null,
        CancellationToken ct = default)
    {
        var read = readOptions ?? options.DefaultArchiveRead;
        var sandboxRootFull = LongPath.Canonical(sandboxRoot);
        var filesRoot = Path.Combine(sandboxRootFull, "files");
        Directory.CreateDirectory(LongPath.Ensure(filesRoot));

        var archiveInfo = await InspectArchiveAsync(archivePath, read, ct).ConfigureAwait(false);
        if (!archiveInfo.IsExtractable)
        {
            Delete(sandboxRootFull);
            throw new PatchRejectedException(
                archiveInfo.RequiresManualRun ? PatchRejectionCode.SelfExtractingExecutable : PatchRejectionCode.UnsupportedFormat,
                archiveInfo.RequiresManualRun
                    ? "Self-extracting executables are never executed or auto-extracted by Galbox."
                    : $"Archive format '{archiveInfo.FormatId}' cannot be extracted.",
                archiveInfo.ManualRunAdvice);
        }

        var files = new List<ExtractedArchiveFile>();
        var warnings = new List<string>(archiveInfo.Warnings);
        var rejected = new List<RejectedArchiveEntry>();
        var takenTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        var opened = PatchArchiveReaderFactory.Open(archivePath, read);
        try
        {
            var source = opened.Source;
            if (source.IsEncrypted)
            {
                // Volume/entry flags are advisory only: SharpCompress sets them for some archives that are
                // perfectly readable (e.g. 7z files with zero-length streams). Extraction is attempted and a
                // real password failure is translated into a refusal further down, so a false positive can
                // never block a legitimate patch.
                warnings.Add("The container declares encrypted entries; extraction will be stopped if the content really is password protected.");
            }

            if (source.Entries.Count > read.MaxEntryCount)
            {
                Delete(sandboxRootFull);
                throw new PatchSecurityException(
                    PatchSecurityCode.ArchiveBudgetExceeded,
                    $"The archive declares {source.Entries.Count} entries, above the configured limit of {read.MaxEntryCount}.");
            }

            foreach (var entry in source.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsDirectory) continue;

                if (entry.IsLink)
                {
                    rejected.Add(new RejectedArchiveEntry
                    {
                        ArchiveEntryIndex = entry.Index,
                        ArchiveEntryName = entry.Name,
                        Code = PatchSecurityCode.LinkEntry,
                        Reason = "Symbolic/hard link entries are refused: they can be used to write outside the destination."
                    });
                    continue;
                }

                var safety = PathSafety.Evaluate(entry.Name, options.NamePolicy);
                if (safety.Rejected)
                {
                    rejected.Add(new RejectedArchiveEntry
                    {
                        ArchiveEntryIndex = entry.Index,
                        ArchiveEntryName = entry.Name,
                        Code = safety.Code,
                        Reason = safety.RejectionReason ?? "Rejected by the path safety check."
                    });
                    continue;
                }

                var relative = safety.RelativePath!;
                if (takenTargets.TryGetValue(relative, out var first))
                {
                    rejected.Add(new RejectedArchiveEntry
                    {
                        ArchiveEntryIndex = entry.Index,
                        ArchiveEntryName = entry.Name,
                        Code = PatchSecurityCode.DuplicateTarget,
                        Reason = $"Another archive entry ('{first}') already resolves to the same destination '{relative}'."
                    });
                    continue;
                }
                takenTargets[relative] = entry.Name;

                // Final containment assertion: the canonical destination must live under the sandbox.
                var destination = LongPath.CombineUnder(filesRoot, relative);

                long size;
                string hash;
                try
                {
                    await using var stream = source.OpenEntry(entry);
                    (size, hash) = await PatchHashing.CopyAndHashAsync(stream, destination, ct).ConfigureAwait(false);
                }
                catch (SharpCompress.Common.CryptographicException ex)
                {
                    Delete(sandboxRootFull);
                    throw new PatchRejectedException(
                        PatchRejectionCode.EncryptedArchive,
                        $"The archive is password protected and cannot be extracted: {ex.Message}",
                        "Extract it manually with the password, then hand Galbox the unpacked contents repacked as a .zip.");
                }

                totalBytes += size;
                if (totalBytes > read.MaxTotalUncompressedBytes)
                {
                    Delete(sandboxRootFull);
                    throw new PatchSecurityException(
                        PatchSecurityCode.ArchiveBudgetExceeded,
                        $"Extraction exceeded the configured budget of {read.MaxTotalUncompressedBytes} bytes; the archive may be a decompression bomb.");
                }

                if (entry.SizeBytes > 0 && entry.SizeBytes != size)
                {
                    warnings.Add($"'{entry.Name}' declared {entry.SizeBytes} bytes but {size} were extracted.");
                }

                files.Add(new ExtractedArchiveFile
                {
                    ArchiveEntryIndex = entry.Index,
                    ArchiveEntryName = entry.Name,
                    RelativePath = relative,
                    SandboxFullPath = destination,
                    SizeBytes = size,
                    Sha256 = hash,
                    NameSanitized = safety.Sanitized,
                    SanitizationNote = safety.SanitizationNote
                });
            }
        }
        catch
        {
            Delete(sandboxRootFull);
            throw;
        }
        finally
        {
            opened.Source.Dispose();
        }

        foreach (var note in opened.Notes)
        {
            warnings.Add(note);
        }

        if (files.Count == 0)
        {
            Delete(sandboxRootFull);
            throw new PatchRejectedException(
                PatchRejectionCode.NothingToInstall,
                rejected.Count > 0
                    ? "Every entry in the archive was rejected; there is nothing safe to install."
                    : "The archive contains no files.");
        }

        return new PatchSandboxResult
        {
            SandboxRoot = sandboxRootFull,
            FilesRoot = filesRoot,
            Files = files,
            Rejected = rejected,
            Archive = archiveInfo,
            TotalBytes = totalBytes,
            Warnings = warnings
        };
    }

    /// <summary>Identifies a package without extracting it.</summary>
    public static async Task<PatchArchiveInfo> InspectArchiveAsync(string archivePath, PatchArchiveReadOptions read, CancellationToken ct = default)
    {
        if (!File.Exists(LongPath.Ensure(archivePath)))
        {
            throw new FileNotFoundException($"Patch package not found: '{archivePath}'.", archivePath);
        }

        var kind = PatchArchiveReaderFactory.DetectKind(archivePath);
        var fileInfo = new FileInfo(LongPath.Ensure(archivePath));
        var sha256 = await PatchHashing.HashFileAsync(archivePath, ct).ConfigureAwait(false);

        var warnings = new List<string>();
        var notes = new List<string>();
        var effective = read.NameEncoding == PatchNameEncoding.Auto ? PatchNameEncoding.Utf8 : read.NameEncoding;
        var autoDetected = read.NameEncoding == PatchNameEncoding.Auto;
        var entryCount = 0;
        var fileCount = 0;
        long uncompressed = 0;
        var encrypted = false;
        var formatId = PatchArchiveReaderFactory.FormatIdOf(kind);

        if (kind is PatchArchiveKind.Zip or PatchArchiveKind.Rar or PatchArchiveKind.SevenZip or PatchArchiveKind.Tar or PatchArchiveKind.Gzip)
        {
            try
            {
                var opened = PatchArchiveReaderFactory.Open(archivePath, read);
                try
                {
                    effective = opened.EffectiveEncoding;
                    autoDetected = opened.AutoDetected;
                    formatId = opened.Source.FormatId;
                    notes.AddRange(opened.Notes);
                    entryCount = opened.Source.Entries.Count;
                    fileCount = opened.Source.Entries.Count(e => !e.IsDirectory);
                    uncompressed = opened.Source.Entries.Where(e => !e.IsDirectory).Sum(e => Math.Max(0, e.SizeBytes));
                    encrypted = opened.Source.IsEncrypted;
                }
                finally
                {
                    opened.Source.Dispose();
                }
            }
            catch (PatchRejectedException ex)
            {
                warnings.Add(ex.Message);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                warnings.Add($"The container could not be opened: {ex.Message}");
            }
        }
        else if (kind == PatchArchiveKind.SelfExtractingExecutable)
        {
            warnings.Add("This is a self-extracting executable (SFX): Galbox will not run it and will not auto-extract it.");
        }

        var requiresManualRun = kind == PatchArchiveKind.SelfExtractingExecutable;

        return new PatchArchiveInfo
        {
            SourcePath = LongPath.Canonical(archivePath),
            FileName = fileInfo.Name,
            SizeBytes = fileInfo.Length,
            Sha256 = sha256,
            Kind = kind,
            FormatId = formatId,
            RequestedNameEncoding = read.NameEncoding,
            EffectiveNameEncoding = effective,
            NameEncodingWasAutoDetected = autoDetected,
            EncodingNotes = notes,
            EntryCount = entryCount,
            FileEntryCount = fileCount,
            TotalUncompressedBytes = uncompressed,
            IsEncrypted = encrypted,
            RequiresManualRun = requiresManualRun,
            ManualRunAdvice = requiresManualRun
                ? "Run the installer yourself inside a dedicated, isolated directory and point it at the game only if you trust it - or unpack it with 7-Zip and hand the resulting archive to Galbox."
                : null,
            Warnings = warnings
        };
    }

    /// <summary>Removes a sandbox directory.</summary>
    public static void Delete(string sandboxRoot)
    {
        if (string.IsNullOrEmpty(sandboxRoot)) return;
        try
        {
            var full = LongPath.Ensure(sandboxRoot);
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
        catch (IOException)
        {
            // A sandbox that cannot be removed is harmless; it lives under the temp/patchsink root.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes every sandbox older than the given age (startup hygiene).</summary>
    public static int SweepStale(string sandboxRoot, TimeSpan olderThan)
    {
        if (!Directory.Exists(LongPath.Ensure(sandboxRoot))) return 0;
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(LongPath.Ensure(sandboxRoot)))
        {
            try
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(LongPath.Ensure(dir)) < olderThan) continue;
                Directory.Delete(LongPath.Ensure(dir), recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort only.
            }
        }
        return removed;
    }
}
