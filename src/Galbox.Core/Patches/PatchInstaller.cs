namespace Galbox.Core.Patches;

/// <summary>
/// The local patch installer. It is deliberately free of UI, network and database concerns:
/// it consumes a downloaded file plus a game directory and produces serialisable results.
/// </summary>
public interface IPatchEngine
{
    /// <summary>Identifies a package without extracting it.</summary>
    Task<PatchArchiveInfo> InspectAsync(string archivePath, PatchArchiveReadOptions? readOptions = null, CancellationToken ct = default);

    /// <summary>Unpacks into a sandbox and computes the overwrite preview.</summary>
    Task<OverwritePreview> PreviewAsync(PatchPreviewRequest request, CancellationToken ct = default);

    /// <summary>Non-throwing preview.</summary>
    Task<PatchPreviewAttempt> TryPreviewAsync(PatchPreviewRequest request, CancellationToken ct = default);

    /// <summary>Applies an approved preview.</summary>
    Task<PatchInstallResult> InstallAsync(PatchInstallRequest request, IProgress<PatchProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Undoes an install; backups are kept so the rollback can be repeated.</summary>
    Task<PatchRollbackResult> RollbackAsync(PatchRollbackRequest request, IProgress<PatchProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Reports whether a patch is installed, changed, interrupted or merely suspected.</summary>
    Task<PatchStatusReport> GetStatusAsync(PatchStatusRequest request, CancellationToken ct = default);

    /// <summary>Finds installs that were interrupted, with a dry-run recovery plan for each.</summary>
    Task<IReadOnlyList<PatchRecoveryPlan>> FindInterruptedAsync(string gameRoot, string? gameId = null, CancellationToken ct = default);

    /// <summary>Applies (or dry-runs) a recovery plan.</summary>
    Task<PatchRollbackResult> RecoverAsync(PatchRecoverRequest request, IProgress<PatchProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IPatchEngine"/> implementation.
/// <para>Shape of the whole flow:</para>
/// <code>
/// preview = await engine.PreviewAsync(...)      // sandbox extract + landing-spot list + conflict grading
/// result  = await engine.InstallAsync(preview)  // journal -&gt; backup overwritten files -&gt; write -&gt; read back -&gt; commit
/// status  = await engine.GetStatusAsync(...)    // ledger + hash re-check; heuristics only ever say "suspected"
/// undo    = await engine.RollbackAsync(...)     // restore backups + delete created files, then hash-proof it
/// </code>
/// </summary>
public sealed class PatchInstaller : IPatchEngine
{
    private readonly PatchInstallerOptions _options;
    private readonly PatchLedger _ledger;
    private readonly PatchBackupStore _backup;

    /// <summary>Creates an installer. With no options the defaults are already conservative.</summary>
    public PatchInstaller(PatchInstallerOptions? options = null)
    {
        _options = options ?? new PatchInstallerOptions();
        _ledger = new PatchLedger(_options);
        _backup = new PatchBackupStore(_options);
    }

    /// <summary>Options in force.</summary>
    public PatchInstallerOptions Options => _options;

    /// <summary>The ledger used by this instance.</summary>
    public PatchLedger Ledger => _ledger;

    /// <summary>The backup store used by this instance.</summary>
    public PatchBackupStore Backups => _backup;

    /// <inheritdoc />
    public Task<PatchArchiveInfo> InspectAsync(string archivePath, PatchArchiveReadOptions? readOptions = null, CancellationToken ct = default)
        => PatchSandbox.InspectArchiveAsync(archivePath, readOptions ?? _options.DefaultArchiveRead, ct);

    /// <inheritdoc />
    public async Task<OverwritePreview> PreviewAsync(PatchPreviewRequest request, CancellationToken ct = default)
    {
        // Gate 1: declared type. A "save" resource never goes through the overlay path.
        PatchAcceptance.EnsureNotDeclaredSave(request.DeclaredTypes);

        var gameRoot = LongPath.Canonical(request.GameRoot);
        if (!Directory.Exists(LongPath.Ensure(gameRoot)))
        {
            throw new DirectoryNotFoundException($"Game directory not found: '{LongPath.Strip(gameRoot)}'.");
        }

        PatchBackupStore.AssertOutsideGameRoot(_options.BackupRoot, gameRoot);

        var readOptions = request.ArchiveRead ?? _options.DefaultArchiveRead;
        var previewId = PatchLedger.NewInstallId();
        var sandboxRootOption = request.SandboxRoot ?? _options.SandboxRoot;
        var sandboxRoot = PatchSandbox.NewSandboxPath(sandboxRootOption, previewId);

        // Gate 2: unpack into the sandbox. Zip Slip attempts are rejected here and never reach the game.
        var sandbox = await PatchSandbox.ExtractAsync(request.ArchivePath, sandboxRoot, _options, readOptions, ct)
            .ConfigureAwait(false);

        try
        {
            // Gate 3: content sniffing - refuse save-data packs that were not declared as such.
            PatchAcceptance.EnsureNotSaveLikeContent(sandbox.Files, _options.AllowSaveLikeContent);

            var warnings = new List<string>(sandbox.Warnings);
            var subDirectory = NormalizeSubDirectory(request.TargetSubDirectory, warnings);
            var managedIndex = await BuildManagedIndexAsync(gameRoot, request.GameId, ct).ConfigureAwait(false);

            var entries = new List<PatchPreviewEntry>(sandbox.Files.Count);
            foreach (var file in sandbox.Files)
            {
                ct.ThrowIfCancellationRequested();

                var relative = string.IsNullOrEmpty(subDirectory)
                    ? file.RelativePath
                    : Path.Combine(subDirectory, file.RelativePath);
                relative = SanitizeRelative(relative, file.ArchiveEntryName, warnings, out var extraSanitized, out var extraNote);

                var targetFull = LongPath.CombineUnder(gameRoot, relative);
                var existing = await DescribeExistingAsync(targetFull, ct).ConfigureAwait(false);

                PatchPreviewAction action;
                PatchTargetProvenance provenance;
                string? reason;

                if (existing is null)
                {
                    action = PatchPreviewAction.Create;
                    provenance = PatchTargetProvenance.NotPresent;
                    reason = "Target does not exist yet.";
                }
                else if (existing.IsDirectory)
                {
                    action = PatchPreviewAction.Conflict;
                    provenance = PatchTargetProvenance.UnknownOrigin;
                    reason = "A directory with this name already exists at the landing path, so the file cannot be written.";
                }
                else if (string.Equals(existing.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    action = PatchPreviewAction.Unchanged;
                    provenance = PatchTargetProvenance.NotPresent;
                    reason = "Target already holds exactly these bytes; nothing would change.";
                }
                else
                {
                    (provenance, reason) = ClassifyProvenance(relative, existing.Sha256!, request.KnownOriginalHashes, managedIndex);
                    action = provenance == PatchTargetProvenance.UnknownOrigin
                        ? PatchPreviewAction.Conflict
                        : PatchPreviewAction.Overwrite;
                }

                entries.Add(new PatchPreviewEntry
                {
                    ArchiveEntryIndex = file.ArchiveEntryIndex,
                    ArchiveEntryName = file.ArchiveEntryName,
                    SandboxRelativePath = file.RelativePath,
                    TargetRelativePath = relative,
                    TargetFullPath = targetFull,
                    Action = action,
                    Provenance = provenance,
                    AssessmentReason = reason,
                    ExistingSizeBytes = existing?.SizeBytes,
                    ExistingSha256 = existing?.Sha256,
                    IncomingSizeBytes = file.SizeBytes,
                    IncomingSha256 = file.Sha256,
                    NameSanitized = file.NameSanitized || extraSanitized,
                    SanitizationNote = file.SanitizationNote ?? extraNote
                });
            }

            var summary = new PatchPreviewSummary
            {
                CreateCount = entries.Count(e => e.Action == PatchPreviewAction.Create),
                OverwriteCount = entries.Count(e => e.Action == PatchPreviewAction.Overwrite),
                UnchangedCount = entries.Count(e => e.Action == PatchPreviewAction.Unchanged),
                ConflictCount = entries.Count(e => e.Action == PatchPreviewAction.Conflict),
                RejectedCount = sandbox.Rejected.Count,
                BytesToWrite = entries.Where(e => e.Action is PatchPreviewAction.Create or PatchPreviewAction.Overwrite or PatchPreviewAction.Conflict)
                                       .Sum(e => e.IncomingSizeBytes),
                BytesToBackup = entries.Where(e => e.Action is PatchPreviewAction.Overwrite or PatchPreviewAction.Conflict)
                                       .Sum(e => e.ExistingSizeBytes ?? 0)
            };

            var sanitizedCount = entries.Count(e => e.NameSanitized);
            if (sanitizedCount > 0)
            {
                warnings.Add($"{sanitizedCount} entry name(s) had to be normalised to be representable on Windows; see ShowName for each line.");
            }
            if (summary.ConflictCount > 0)
            {
                warnings.Add($"{summary.ConflictCount} target file(s) exist with unknown provenance; the user must confirm them before they are replaced.");
            }
            if (sandbox.Rejected.Count > 0)
            {
                warnings.Add($"{sandbox.Rejected.Count} archive entry/entries were refused and will not be written (see Rejected).");
            }

            var preview = new OverwritePreview
            {
                PreviewId = previewId,
                Archive = sandbox.Archive,
                GameRoot = gameRoot,
                GameId = request.GameId,
                PatchName = request.PatchName,
                DeclaredTypes = request.DeclaredTypes,
                Languages = request.Languages,
                Platforms = request.Platforms,
                Source = request.Source,
                TargetSubDirectory = string.IsNullOrEmpty(subDirectory) ? null : subDirectory,
                SandboxRoot = sandbox.SandboxRoot,
                SandboxFilesRoot = sandbox.FilesRoot,
                Entries = entries,
                Rejected = sandbox.Rejected,
                Summary = summary,
                Warnings = warnings
            };

            await SavePreviewAsync(preview, ct).ConfigureAwait(false);
            Log($"Preview {previewId}: {summary.CreateCount} create / {summary.OverwriteCount} overwrite / {summary.ConflictCount} conflict / {summary.UnchangedCount} unchanged / {summary.RejectedCount} rejected.");
            return preview;
        }
        catch
        {
            PatchSandbox.Delete(sandboxRoot);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<PatchPreviewAttempt> TryPreviewAsync(PatchPreviewRequest request, CancellationToken ct = default)
    {
        try
        {
            var preview = await PreviewAsync(request, ct).ConfigureAwait(false);
            return new PatchPreviewAttempt { Succeeded = true, Preview = preview, Archive = preview.Archive };
        }
        catch (PatchRejectedException ex)
        {
            var archive = await TryInspectQuietlyAsync(request, ct).ConfigureAwait(false);
            return new PatchPreviewAttempt
            {
                Succeeded = false,
                Archive = archive,
                RejectionCode = ex.Code,
                Message = ex.Message,
                Remedy = ex.Remedy
            };
        }
        catch (PatchSecurityException ex)
        {
            var archive = await TryInspectQuietlyAsync(request, ct).ConfigureAwait(false);
            return new PatchPreviewAttempt
            {
                Succeeded = false,
                Archive = archive,
                RejectionCode = PatchRejectionCode.CorruptArchive,
                SecurityCode = ex.Code,
                Message = ex.Message
            };
        }
    }

    /// <summary>Persists a preview next to its sandbox so another process can pick the flow up.</summary>
    public static async Task<string> SavePreviewAsync(OverwritePreview preview, CancellationToken ct = default)
    {
        Directory.CreateDirectory(LongPath.Ensure(preview.SandboxRoot));
        var path = Path.Combine(preview.SandboxRoot, "preview.json");
        await File.WriteAllTextAsync(LongPath.Ensure(path), PatchJson.Serialize(preview), ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>Reloads a preview written by <see cref="SavePreviewAsync"/>.</summary>
    public static OverwritePreview? LoadPreview(string sandboxRoot)
    {
        var path = Path.Combine(sandboxRoot, "preview.json");
        if (!File.Exists(LongPath.Ensure(path))) return null;
        try
        {
            return PatchJson.Deserialize<OverwritePreview>(File.ReadAllText(LongPath.Ensure(path)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<PatchInstallResult> InstallAsync(
        PatchInstallRequest request,
        IProgress<PatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        var preview = request.Preview;
        var decisions = request.Decisions ?? PatchInstallDecisions.None;
        var gameRoot = LongPath.Canonical(preview.GameRoot);
        var installId = PatchLedger.NewInstallId();
        var warnings = new List<string>();
        var errors = new List<string>();
        var operations = new List<PatchFileOperationResult>();

        if (!Directory.Exists(LongPath.Ensure(gameRoot)))
        {
            return Failure(installId, errors, $"Game directory not found: '{LongPath.Strip(gameRoot)}'.");
        }

        try
        {
            PatchBackupStore.AssertOutsideGameRoot(_options.BackupRoot, gameRoot);
        }
        catch (PatchSecurityException ex)
        {
            return Failure(installId, errors, ex.Message);
        }

        var excluded = new HashSet<string>(decisions.ExcludedPaths, StringComparer.OrdinalIgnoreCase);
        var confirmed = new HashSet<string>(decisions.ConfirmedConflicts, StringComparer.OrdinalIgnoreCase);

        // Conflicts are never overwritten silently.
        var blocking = preview.Entries
            .Where(e => e.Action == PatchPreviewAction.Conflict && !excluded.Contains(e.TargetRelativePath) && !confirmed.Contains(e.TargetRelativePath))
            .Select(e => e.TargetRelativePath)
            .ToList();

        if (blocking.Count > 0 && request.RequireConflictConfirmation)
        {
            return new PatchInstallResult
            {
                Success = false,
                InstallId = installId,
                BlockingConflicts = blocking,
                Errors = new[] { $"{blocking.Count} file(s) exist with unknown provenance and were not confirmed by the user. Nothing was written." }
            };
        }

        var planned = preview.Entries
            .Where(e => !excluded.Contains(e.TargetRelativePath))
            .Where(e => e.Action is PatchPreviewAction.Create or PatchPreviewAction.Overwrite
                        || (e.Action == PatchPreviewAction.Conflict && confirmed.Contains(e.TargetRelativePath)))
            .ToList();

        var unchanged = preview.Entries.Where(e => e.Action == PatchPreviewAction.Unchanged).ToList();
        foreach (var entry in unchanged)
        {
            operations.Add(new PatchFileOperationResult
            {
                RelativePath = entry.TargetRelativePath,
                Action = PatchPreviewAction.Unchanged,
                Status = PatchFileOperationStatus.Skipped,
                HashAfter = entry.IncomingSha256,
                Message = "Already identical; the file was left untouched."
            });
        }

        // --- Phase 0: revalidate sandbox and targets ------------------------------------------------
        // Guards against a tampered sandbox or a game update that happened between preview and install.
        foreach (var entry in planned)
        {
            ct.ThrowIfCancellationRequested();
            var sandboxFile = LongPath.CombineUnder(preview.SandboxFilesRoot, entry.SandboxRelativePath ?? entry.TargetRelativePath);
            var sandboxHash = await PatchHashing.TryHashFileAsync(sandboxFile, ct).ConfigureAwait(false);
            if (!string.Equals(sandboxHash, entry.IncomingSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Failure(installId, errors,
                    $"The staged file for '{entry.TargetRelativePath}' no longer matches the preview (expected {entry.IncomingSha256}, found {sandboxHash ?? "nothing"}). Nothing was written; please preview again.");
            }

            if (!request.RevalidateTargets) continue;

            var current = await DescribeExistingAsync(entry.TargetFullPath, ct).ConfigureAwait(false);
            if (entry.Action == PatchPreviewAction.Create)
            {
                if (current is not null)
                {
                    return Failure(installId, errors,
                        $"'{entry.TargetRelativePath}' was created by something else after the preview. Nothing was written; please preview again.");
                }
            }
            else if (current is null || !string.Equals(current.Sha256, entry.ExistingSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Failure(installId, errors,
                    $"'{entry.TargetRelativePath}' changed after the preview (expected {entry.ExistingSha256 ?? "no file"}, found {current?.Sha256 ?? "no file"}). Nothing was written; please preview again.");
            }
        }

        // --- Phase 1: write the journal (a pending manifest) BEFORE touching anything ----------------
        var createdDirectories = new List<string>();
        var plannedEntries = new List<PatchManifestFileEntry>(planned.Count);
        foreach (var entry in planned)
        {
            var action = entry.Action == PatchPreviewAction.Create ? PatchFileAction.Created : PatchFileAction.Overwritten;
            plannedEntries.Add(new PatchManifestFileEntry
            {
                Rel = entry.TargetRelativePath,
                Action = action,
                BackupRel = action == PatchFileAction.Overwritten ? PatchBackupStore.BackupRelFor(entry.TargetRelativePath) : null,
                SizeBefore = entry.ExistingSizeBytes,
                HashBefore = entry.ExistingSha256,
                SizeAfter = entry.IncomingSizeBytes,
                HashAfter = entry.IncomingSha256,
                VerifiedAfterWrite = false,
                VerificationNote = "Pending: the install has not committed yet."
            });
        }

        var pendingManifest = new PatchManifest
        {
            InstallId = installId,
            GameId = preview.GameId,
            GameRoot = gameRoot,
            TargetSubDirectory = preview.TargetSubDirectory,
            PatchName = preview.PatchName,
            DeclaredTypes = preview.DeclaredTypes,
            Languages = preview.Languages,
            Platforms = preview.Platforms,
            Source = preview.Source,
            Archive = ToArchiveRef(preview),
            Status = PatchManifestStatus.Pending,
            Files = plannedEntries,
            Warnings = warnings,
            EngineVersion = PatchLedger.EngineVersion,
            StatusNote = "Journal written before the first write; a crash in this state is detected as an interrupted install."
        };

        var manifestPath = await _ledger.WriteManifestAsync(pendingManifest, ct).ConfigureAwait(false);
        Log($"Install {installId}: journal written to {manifestPath}");

        // --- Phase 2: back up only the files that are about to be replaced ---------------------------
        var backups = new Dictionary<string, PatchBackupRecord>(StringComparer.OrdinalIgnoreCase);
        var overwritePlan = planned.Where(e => e.Action != PatchPreviewAction.Create).ToList();
        var index = 0;
        foreach (var entry in overwritePlan)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new PatchProgress
            {
                Phase = "backup",
                Completed = index++,
                Total = overwritePlan.Count,
                CurrentItem = entry.TargetRelativePath,
                Message = "Backing up the files that will be replaced (nothing else is copied)."
            });

            try
            {
                var record = await _backup.BackupAsync(preview.GameId, installId, gameRoot, entry.TargetRelativePath, PatchFileAction.Overwritten, ct)
                    .ConfigureAwait(false);
                backups[entry.TargetRelativePath] = record;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var message = $"Backup of '{entry.TargetRelativePath}' failed: {ex.Message}";
                errors.Add(message);
                await MarkFailedAsync(pendingManifest, message, ct).ConfigureAwait(false);
                return Failure(installId, errors, message, warnings, operations);
            }
        }

        // --- Phase 3: write the files ---------------------------------------------------------------
        var written = new List<PatchManifestFileEntry>();
        var failures = 0;
        index = 0;
        long bytesWritten = 0;
        long bytesBackedUp = backups.Values.Sum(b => b.SizeBytes);

        foreach (var entry in planned)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new PatchProgress
            {
                Phase = "write",
                Completed = index++,
                Total = planned.Count,
                CurrentItem = entry.TargetRelativePath,
                Message = "Writing files into the game directory."
            });

            var action = entry.Action == PatchPreviewAction.Create ? PatchFileAction.Created : PatchFileAction.Overwritten;
            var backupRecord = backups.GetValueOrDefault(entry.TargetRelativePath);

            try
            {
                CollectCreatedDirectories(entry.TargetFullPath, gameRoot, createdDirectories);
                var sandboxFile = LongPath.CombineUnder(preview.SandboxFilesRoot, entry.SandboxRelativePath ?? entry.TargetRelativePath);
                await using (var source = new FileStream(LongPath.Ensure(sandboxFile), FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await PatchHashing.CopyAndHashAsync(source, entry.TargetFullPath, ct).ConfigureAwait(false);
                }

                // Preserve the timestamp carried by the archive, the way a normal unzip would.
                try
                {
                    File.SetLastWriteTimeUtc(LongPath.Ensure(entry.TargetFullPath), File.GetLastWriteTimeUtc(LongPath.Ensure(sandboxFile)));
                }
                catch (IOException)
                {
                }

                bytesWritten += entry.IncomingSizeBytes;

                // Read back from disk: this is what catches antivirus quarantining a freshly written file.
                var readBack = _options.VerifyAfterWrite
                    ? await PatchHashing.TryHashFileAsync(entry.TargetFullPath, ct).ConfigureAwait(false)
                    : entry.IncomingSha256;

                var verified = string.Equals(readBack, entry.IncomingSha256, StringComparison.OrdinalIgnoreCase);
                if (!verified)
                {
                    failures++;
                    var warning = $"'{entry.TargetRelativePath}' did not match after writing (expected {entry.IncomingSha256}, on disk {readBack ?? "missing"}). " +
                                  "This usually means antivirus software quarantined the file.";
                    warnings.Add(warning);
                    Log(warning);
                }

                written.Add(new PatchManifestFileEntry
                {
                    Rel = entry.TargetRelativePath,
                    Action = action,
                    BackupRel = backupRecord?.BackupRel,
                    SizeBefore = entry.ExistingSizeBytes,
                    HashBefore = backupRecord?.Sha256 ?? entry.ExistingSha256,
                    SizeAfter = entry.IncomingSizeBytes,
                    HashAfter = readBack ?? entry.IncomingSha256,
                    VerifiedAfterWrite = verified,
                    VerificationNote = verified ? null : "Read-back mismatch right after writing; the file may have been intervened with."
                });

                operations.Add(new PatchFileOperationResult
                {
                    RelativePath = entry.TargetRelativePath,
                    Action = entry.Action,
                    Status = verified ? PatchFileOperationStatus.Ok : PatchFileOperationStatus.ReadBackMismatch,
                    BackupRel = backupRecord?.BackupRel,
                    HashAfter = readBack,
                    Message = verified ? null : "Read-back hash mismatch - possibly blocked by antivirus software."
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
                errors.Add($"Writing '{entry.TargetRelativePath}' failed: {ex.Message}");
                operations.Add(new PatchFileOperationResult
                {
                    RelativePath = entry.TargetRelativePath,
                    Action = entry.Action,
                    Status = PatchFileOperationStatus.Failed,
                    BackupRel = backupRecord?.BackupRel,
                    Message = ex.Message
                });
            }
        }

        // --- Phase 4: commit ------------------------------------------------------------------------
        var committed = failures == 0;
        var finalManifest = new PatchManifest
        {
            InstallId = installId,
            GameId = preview.GameId,
            GameRoot = gameRoot,
            TargetSubDirectory = preview.TargetSubDirectory,
            PatchName = preview.PatchName,
            DeclaredTypes = preview.DeclaredTypes,
            Languages = preview.Languages,
            Platforms = preview.Platforms,
            Source = preview.Source,
            Archive = ToArchiveRef(preview),
            CreatedAt = pendingManifest.CreatedAt,
            CommittedAt = committed ? DateTimeOffset.UtcNow : null,
            Status = committed ? PatchManifestStatus.Committed : PatchManifestStatus.Failed,
            Files = written,
            CreatedDirectories = createdDirectories,
            Warnings = warnings,
            EngineVersion = PatchLedger.EngineVersion,
            StatusNote = committed
                ? "Committed: every planned file was written and read back."
                : $"{failures} file(s) failed or failed verification; the game directory may be in a mixed state and a rollback is recommended."
        };

        await _ledger.WriteManifestAsync(finalManifest, ct).ConfigureAwait(false);

        progress?.Report(new PatchProgress
        {
            Phase = "verify",
            Completed = planned.Count,
            Total = planned.Count,
            Message = committed ? "All files verified." : $"{failures} file(s) need attention."
        });

        if (!_options.KeepSandboxAfterInstall)
        {
            PatchSandbox.Delete(preview.SandboxRoot);
        }

        var retention = _backup.PlanRetention(gameRoot, preview.GameId);
        Log($"Install {installId}: committed={committed}, created={written.Count(f => f.Action == PatchFileAction.Created)}, overwritten={written.Count(f => f.Action == PatchFileAction.Overwritten)}.");

        return new PatchInstallResult
        {
            Success = committed,
            InstallId = installId,
            Manifest = finalManifest,
            ManifestPath = _ledger.ManifestPath(preview.GameId, installId),
            InGameManifestPath = _ledger.GameBundlePath(gameRoot),
            Operations = operations,
            CreatedCount = written.Count(f => f.Action == PatchFileAction.Created),
            OverwrittenCount = written.Count(f => f.Action == PatchFileAction.Overwritten),
            BytesWritten = bytesWritten,
            BytesBackedUp = bytesBackedUp,
            Errors = errors,
            Warnings = warnings,
            RollbackAvailable = true,
            Retention = retention
        };
    }

    /// <inheritdoc />
    public async Task<PatchRollbackResult> RollbackAsync(
        PatchRollbackRequest request,
        IProgress<PatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        var gameRoot = LongPath.Canonical(request.GameRoot);
        PatchManifest? manifest;
        if (string.IsNullOrEmpty(request.InstallId))
        {
            manifest = _ledger.ReadLatestActive(gameRoot, request.GameId);
        }
        else
        {
            manifest = _ledger.ReadInstall(gameRoot, request.GameId, request.InstallId!);
        }

        if (manifest is null)
        {
            return new PatchRollbackResult
            {
                Success = true,
                InstallId = request.InstallId ?? "(latest)",
                NothingToDo = true,
                Warnings = new[] { "No Galbox manifest was found for this game, so there is nothing Galbox can undo." }
            };
        }

        return await RollbackCoreAsync(manifest, request, progress, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PatchStatusReport> GetStatusAsync(PatchStatusRequest request, CancellationToken ct = default)
    {
        var evaluator = new PatchStatusEvaluator(_options, _ledger);
        return await evaluator.EvaluateAsync(request, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PatchRecoveryPlan>> FindInterruptedAsync(string gameRoot, string? gameId = null, CancellationToken ct = default)
    {
        var root = LongPath.Canonical(gameRoot);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(gameId)) ids.Add(gameId!);

        // Also discover pending installs purely from the in-game bundle, which is what a machine swap looks like.
        var bundle = _ledger.ReadGameBundle(root);
        if (bundle is not null) ids.Add(bundle.GameId);

        if (Directory.Exists(LongPath.Ensure(_options.BackupRoot)))
        {
            foreach (var dir in Directory.EnumerateDirectories(LongPath.Ensure(_options.BackupRoot)))
            {
                foreach (var installDir in Directory.EnumerateDirectories(dir))
                {
                    var manifest = PatchLedger.TryReadManifest(Path.Combine(installDir, _options.LedgerManifestFileName));
                    if (manifest is null) continue;
                    if (!string.Equals(LongPath.Canonical(manifest.GameRoot), root, StringComparison.OrdinalIgnoreCase)) continue;
                    ids.Add(manifest.GameId);
                }
            }
        }

        var plans = new List<PatchRecoveryPlan>();
        foreach (var id in ids)
        {
            foreach (var manifest in _ledger.ReadAllInstalls(root, id))
            {
                ct.ThrowIfCancellationRequested();
                if (manifest.Status is not (PatchManifestStatus.Pending or PatchManifestStatus.Failed)) continue;
                if (!string.Equals(LongPath.Canonical(manifest.GameRoot), root, StringComparison.OrdinalIgnoreCase)) continue;
                plans.Add(await BuildRecoveryPlanAsync(manifest, ct).ConfigureAwait(false));
            }
        }

        return plans.OrderBy(p => p.StartedAt ?? DateTimeOffset.MinValue).ToList();
    }

    /// <inheritdoc />
    public async Task<PatchRollbackResult> RecoverAsync(
        PatchRecoverRequest request,
        IProgress<PatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        var plan = request.Plan;
        if (!request.Apply)
        {
            return new PatchRollbackResult
            {
                Success = true,
                InstallId = plan.InstallId,
                NothingToDo = true,
                Warnings = new[] { "Dry run: the recovery plan was not applied." },
                Manifest = plan.Manifest
            };
        }

        var rollbackRequest = new PatchRollbackRequest
        {
            GameRoot = plan.GameRoot,
            GameId = plan.GameId,
            InstallId = plan.InstallId,
            // An interrupted install never committed, so anything it left behind belongs to it - including a
            // half-written file that no longer matches the planned hash. A committed install stays conservative
            // and leaves user-modified files alone, which is a different situation entirely.
            DeleteModifiedCreatedFiles = true
        };

        return await RollbackCoreAsync(plan.Manifest, rollbackRequest, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Computes a dry-run recovery plan for an interrupted install.</summary>
    public async Task<PatchRecoveryPlan> BuildRecoveryPlanAsync(PatchManifest manifest, CancellationToken ct = default)
    {
        var steps = new List<string>();
        int restorable = 0, removable = 0, indeterminate = 0;

        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (file.Action == PatchFileAction.Overwritten)
            {
                var backupFull = string.IsNullOrEmpty(file.BackupRel)
                    ? null
                    : LongPath.CombineUnder(_ledger.InstallFolder(manifest.GameId, manifest.InstallId), file.BackupRel);
                var ok = backupFull is not null && File.Exists(LongPath.Ensure(backupFull)) &&
                         (file.HashBefore is null || await PatchHashing.VerifyFileAsync(backupFull, file.HashBefore, ct).ConfigureAwait(false));
                if (ok)
                {
                    restorable++;
                    steps.Add($"restore '{file.Rel}' from its verified backup");
                }
                else
                {
                    indeterminate++;
                    steps.Add($"'{file.Rel}' cannot be restored: its backup is missing or corrupt");
                }
            }
            else
            {
                var target = LongPath.CombineUnder(manifest.GameRoot, file.Rel);
                if (!File.Exists(LongPath.Ensure(target)))
                {
                    removable++;
                    steps.Add($"'{file.Rel}' was never written; nothing to remove");
                    continue;
                }
                var hash = await PatchHashing.TryHashFileAsync(target, ct).ConfigureAwait(false);
                if (string.Equals(hash, file.HashAfter, StringComparison.OrdinalIgnoreCase))
                {
                    removable++;
                    steps.Add($"delete '{file.Rel}' (created by the interrupted install)");
                }
                else
                {
                    removable++;
                    steps.Add($"delete '{file.Rel}' - left behind by the interrupted install; its content does not match the plan, so the write was partial");
                }
            }
        }

        return new PatchRecoveryPlan
        {
            InstallId = manifest.InstallId,
            GameId = manifest.GameId,
            GameRoot = manifest.GameRoot,
            PatchName = manifest.PatchName,
            StartedAt = manifest.CommittedAt ?? manifest.CreatedAt,
            Status = manifest.Status,
            Reason = manifest.Status == PatchManifestStatus.Pending
                ? $"The install started at {manifest.CreatedAt:u} but never reached the committed state; it was interrupted (power loss, crash or kill) and must be rolled back."
                : $"The install recorded a failure at {manifest.CreatedAt:u} and left the game directory in a mixed state.",
            RestorableFileCount = restorable,
            RemovableFileCount = removable,
            IndeterminateFileCount = indeterminate,
            Steps = steps,
            Manifest = manifest
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------------

    private async Task<PatchRollbackResult> RollbackCoreAsync(
        PatchManifest manifest,
        PatchRollbackRequest request,
        IProgress<PatchProgress>? progress,
        CancellationToken ct)
    {
        var gameRoot = LongPath.Canonical(manifest.GameRoot);
        var results = new List<PatchRestoreRecord>();
        var errors = new List<string>();
        var warnings = new List<string>();

        if (manifest.Status == PatchManifestStatus.RolledBack)
        {
            warnings.Add("This install was already rolled back once. The backups are kept on purpose, so the rollback can be repeated safely.");
        }

        var index = 0;
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new PatchProgress
            {
                Phase = "rollback",
                Completed = index++,
                Total = manifest.Files.Count,
                CurrentItem = file.Rel,
                Message = "Restoring the previous state."
            });

            if (file.Action == PatchFileAction.Overwritten)
            {
                var record = await _backup.RestoreAsync(manifest.GameId, manifest.InstallId, gameRoot, file, ct).ConfigureAwait(false);
                results.Add(record);
                if (record.Outcome is PatchRestoreOutcome.Failed or PatchRestoreOutcome.VerificationFailed)
                {
                    errors.Add($"{file.Rel}: {record.Message}");
                }
                continue;
            }

            // Created by the install: remove it, but never silently delete something the user changed.
            var target = LongPath.CombineUnder(gameRoot, file.Rel);
            if (!File.Exists(LongPath.Ensure(target)))
            {
                results.Add(new PatchRestoreRecord
                {
                    RelativePath = file.Rel,
                    Outcome = PatchRestoreOutcome.Skipped,
                    ExpectedHash = null,
                    Message = "Already absent."
                });
                continue;
            }

            var currentHash = await PatchHashing.TryHashFileAsync(target, ct).ConfigureAwait(false);
            if (!string.Equals(currentHash, file.HashAfter, StringComparison.OrdinalIgnoreCase) && !request.DeleteModifiedCreatedFiles)
            {
                results.Add(new PatchRestoreRecord
                {
                    RelativePath = file.Rel,
                    Outcome = PatchRestoreOutcome.Skipped,
                    ExpectedHash = file.HashAfter,
                    ActualHash = currentHash,
                    Message = "This file was created by the install but has changed since; it was left alone. " +
                              "Re-run with DeleteModifiedCreatedFiles to remove it anyway."
                });
                warnings.Add($"'{file.Rel}' was modified after the install and was not deleted.");
                continue;
            }

            try
            {
                File.Delete(LongPath.Ensure(target));
                results.Add(new PatchRestoreRecord
                {
                    RelativePath = file.Rel,
                    Outcome = PatchRestoreOutcome.Deleted,
                    ExpectedHash = file.HashAfter,
                    ActualHash = null
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Could not delete '{file.Rel}': {ex.Message}");
                results.Add(new PatchRestoreRecord
                {
                    RelativePath = file.Rel,
                    Outcome = PatchRestoreOutcome.Failed,
                    Message = ex.Message
                });
            }
        }

        var pruned = PruneCreatedDirectories(manifest, gameRoot);

        var overwritten = results.Where(r => manifest.Files.First(f => f.Rel == r.RelativePath).Action == PatchFileAction.Overwritten).ToList();
        var created = results.Where(r => manifest.Files.First(f => f.Rel == r.RelativePath).Action == PatchFileAction.Created).ToList();

        var byteIdentical = errors.Count == 0 &&
                            overwritten.All(r => r.Outcome == PatchRestoreOutcome.Restored && r.ByteIdentical) &&
                            created.All(r => r.Outcome is PatchRestoreOutcome.Deleted or PatchRestoreOutcome.Skipped);

        var rolledBack = new PatchManifest
        {
            InstallId = manifest.InstallId,
            GameId = manifest.GameId,
            GameRoot = manifest.GameRoot,
            TargetSubDirectory = manifest.TargetSubDirectory,
            PatchName = manifest.PatchName,
            DeclaredTypes = manifest.DeclaredTypes,
            Languages = manifest.Languages,
            Platforms = manifest.Platforms,
            Source = manifest.Source,
            Archive = manifest.Archive,
            CreatedAt = manifest.CreatedAt,
            CommittedAt = manifest.CommittedAt,
            RolledBackAt = DateTimeOffset.UtcNow,
            Status = PatchManifestStatus.RolledBack,
            Files = manifest.Files,
            CreatedDirectories = manifest.CreatedDirectories,
            Warnings = manifest.Warnings,
            EngineVersion = manifest.EngineVersion,
            StatusNote = byteIdentical
                ? "Rolled back: every restored file matches its recorded pre-install hash. Backups were intentionally kept."
                : "Rolled back with exceptions; see the per-file results. Backups were intentionally kept."
        };

        await _ledger.WriteManifestAsync(rolledBack, ct).ConfigureAwait(false);

        progress?.Report(new PatchProgress
        {
            Phase = "rollback-verify",
            Completed = manifest.Files.Count,
            Total = manifest.Files.Count,
            Message = byteIdentical ? "Rollback verified byte-for-byte against the pre-install hashes." : "Rollback finished with exceptions."
        });

        return new PatchRollbackResult
        {
            Success = byteIdentical,
            InstallId = manifest.InstallId,
            Files = results,
            RestoredCount = results.Count(r => r.Outcome == PatchRestoreOutcome.Restored),
            DeletedCount = results.Count(r => r.Outcome == PatchRestoreOutcome.Deleted),
            SkippedCount = results.Count(r => r.Outcome == PatchRestoreOutcome.Skipped),
            ByteIdenticalToPreInstall = byteIdentical,
            PrunedDirectories = pruned,
            Manifest = rolledBack,
            Errors = errors,
            Warnings = warnings
        };
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildManagedIndexAsync(string gameRoot, string gameId, CancellationToken ct)
    {
        // Values encode "hash -> who wrote it" so that the preview can explain *why* a file is safe to replace.
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in _ledger.ReadAllInstalls(gameRoot, gameId))
        {
            foreach (var file in manifest.Files)
            {
                if (!string.IsNullOrEmpty(file.HashAfter))
                {
                    index[$"{file.Rel}\u0001{file.HashAfter}"] = $"install {manifest.InstallId} wrote it";
                }
                if (!string.IsNullOrEmpty(file.HashBefore))
                {
                    index[$"{file.Rel}\u0001{file.HashBefore}"] = $"install {manifest.InstallId} recorded this as the original content";
                }
            }
        }
        await Task.CompletedTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return index;
    }

    private static (PatchTargetProvenance Provenance, string Reason) ClassifyProvenance(
        string relativePath,
        string existingHash,
        IReadOnlyDictionary<string, string>? knownOriginalHashes,
        IReadOnlyDictionary<string, string> managedIndex)
    {
        if (knownOriginalHashes is not null &&
            knownOriginalHashes.TryGetValue(relativePath, out var known) &&
            string.Equals(known, existingHash, StringComparison.OrdinalIgnoreCase))
        {
            return (PatchTargetProvenance.KnownOriginal, "The caller supplied this hash as a known original game file.");
        }

        if (managedIndex.TryGetValue($"{relativePath}\u0001{existingHash}", out var explanation))
        {
            return (PatchTargetProvenance.ManagedByGalbox, $"Recognised from the Galbox ledger: {explanation}.");
        }

        return (PatchTargetProvenance.UnknownOrigin,
            "The target exists with different content and neither the ledger nor the caller can explain where it came from (often a hand-installed patch).");
    }

    private static async Task<ExistingPathInfo?> DescribeExistingAsync(string path, CancellationToken ct)
    {
        var extended = LongPath.Ensure(path);
        if (Directory.Exists(extended))
        {
            return new ExistingPathInfo { IsDirectory = true };
        }
        if (!File.Exists(extended)) return null;
        var info = new FileInfo(extended);
        var hash = await PatchHashing.TryHashFileAsync(path, ct).ConfigureAwait(false);
        return new ExistingPathInfo { IsDirectory = false, SizeBytes = info.Length, Sha256 = hash };
    }

    private static string NormalizeSubDirectory(string? subDirectory, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(subDirectory)) return string.Empty;
        var safety = PathSafety.Evaluate(subDirectory!);
        if (safety.Rejected)
        {
            throw new PatchSecurityException(
                safety.Code,
                $"The requested landing sub-directory '{subDirectory}' is not a valid relative path: {safety.RejectionReason}");
        }
        if (safety.Sanitized)
        {
            warnings.Add($"Landing sub-directory was normalised to '{safety.RelativePath}'.");
        }
        return safety.RelativePath!;
    }

    private static string SanitizeRelative(string relative, string originalName, List<string> warnings, out bool sanitized, out string? note)
    {
        var safety = PathSafety.Evaluate(relative);
        if (safety.Rejected)
        {
            throw new PatchSecurityException(safety.Code, $"Landing path '{relative}' (from '{originalName}') is unsafe: {safety.RejectionReason}");
        }
        sanitized = safety.Sanitized;
        note = safety.SanitizationNote;
        if (sanitized) warnings.Add($"'{originalName}' -> '{safety.RelativePath}' ({safety.SanitizationNote})");
        return safety.RelativePath!;
    }

    private static void CollectCreatedDirectories(string targetFullPath, string gameRoot, List<string> createdDirectories)
    {
        var parent = Path.GetDirectoryName(targetFullPath);
        var toCreate = new List<string>();
        while (!string.IsNullOrEmpty(parent) && !Directory.Exists(LongPath.Ensure(parent)))
        {
            if (!LongPath.IsUnder(gameRoot, parent)) break;
            toCreate.Add(parent);
            parent = Path.GetDirectoryName(parent);
        }
        toCreate.Reverse();
        foreach (var directory in toCreate)
        {
            Directory.CreateDirectory(LongPath.Ensure(directory));
            createdDirectories.Add(LongPath.RelativeUnder(gameRoot, directory) ?? directory);
        }
    }

    private static IReadOnlyList<string> PruneCreatedDirectories(PatchManifest manifest, string gameRoot)
    {
        var pruned = new List<string>();
        var directories = manifest.CreatedDirectories
            .Where(d => !string.IsNullOrEmpty(d))
            .OrderByDescending(d => d.Length)
            .ToList();

        foreach (var directory in directories)
        {
            var full = LongPath.CombineUnder(gameRoot, directory);
            try
            {
                if (!Directory.Exists(LongPath.Ensure(full))) continue;
                if (Directory.EnumerateFileSystemEntries(LongPath.Ensure(full)).Any()) continue;
                Directory.Delete(LongPath.Ensure(full));
                pruned.Add(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A directory we cannot prune is harmless.
            }
        }
        return pruned;
    }

    private async Task MarkFailedAsync(PatchManifest pending, string reason, CancellationToken ct)
    {
        var failed = new PatchManifest
        {
            InstallId = pending.InstallId,
            GameId = pending.GameId,
            GameRoot = pending.GameRoot,
            TargetSubDirectory = pending.TargetSubDirectory,
            PatchName = pending.PatchName,
            DeclaredTypes = pending.DeclaredTypes,
            Languages = pending.Languages,
            Platforms = pending.Platforms,
            Source = pending.Source,
            Archive = pending.Archive,
            CreatedAt = pending.CreatedAt,
            Status = PatchManifestStatus.Failed,
            Files = pending.Files,
            CreatedDirectories = pending.CreatedDirectories,
            Warnings = pending.Warnings,
            EngineVersion = pending.EngineVersion,
            StatusNote = reason
        };
        await _ledger.WriteManifestAsync(failed, ct).ConfigureAwait(false);
    }

    private static PatchArchiveRef ToArchiveRef(OverwritePreview preview) => new()
    {
        FileName = preview.Archive.FileName,
        SizeBytes = preview.Archive.SizeBytes,
        Sha256 = preview.Archive.Sha256,
        Format = preview.Archive.FormatId,
        NameEncoding = PatchNameEncodings.ToId(preview.Archive.EffectiveNameEncoding),
        FileEntryCount = preview.Archive.FileEntryCount,
        SourceBlake3 = null
    };

    private static PatchInstallResult Failure(
        string installId,
        List<string> errors,
        string message,
        List<string>? warnings = null,
        List<PatchFileOperationResult>? operations = null)
    {
        errors.Add(message);
        return new PatchInstallResult
        {
            Success = false,
            InstallId = installId,
            Operations = operations ?? new List<PatchFileOperationResult>(),
            Errors = errors,
            Warnings = warnings ?? new List<string>()
        };
    }

    private async Task<PatchArchiveInfo?> TryInspectQuietlyAsync(PatchPreviewRequest request, CancellationToken ct)
    {
        try
        {
            return await InspectAsync(request.ArchivePath, request.ArchiveRead, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Log(string message) => _options.Log?.Invoke($"[patch] {message}");
}
