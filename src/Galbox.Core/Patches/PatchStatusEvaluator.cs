namespace Galbox.Core.Patches;

/// <summary>
/// Decides the status of a patch. The reliability layers follow the product research exactly:
/// <list type="number">
/// <item>Galbox's own ledger, re-verified against the files it wrote - the only source that can say "installed".</item>
/// <item>The archive hash (<c>blake3</c>/<c>sha256</c> of the download) proves nothing about installation and is never used here.</item>
/// <item>Third-party traces in the game directory - may only produce "suspected" or "cannot tell".</item>
/// <item>Update detection is a weak timestamp comparison and is reported as "might have an update", never as a version verdict.</item>
/// </list>
/// The evaluator refuses to emit an authoritative status with non-factual evidence; that invariant is asserted
/// before the report leaves this class.
/// </summary>
public sealed class PatchStatusEvaluator
{
    private readonly PatchInstallerOptions _options;
    private readonly PatchLedger _ledger;
    private readonly HeuristicPatchScanner _scanner = new();

    /// <summary>Creates an evaluator.</summary>
    public PatchStatusEvaluator(PatchInstallerOptions? options = null, PatchLedger? ledger = null)
    {
        _options = options ?? new PatchInstallerOptions();
        _ledger = ledger ?? new PatchLedger(_options);
    }

    /// <summary>Evaluates the status of a game directory.</summary>
    public async Task<PatchStatusReport> EvaluateAsync(PatchStatusRequest request, CancellationToken ct = default)
    {
        // The caller's own package metadata already tells us this is save data: the overlay installer
        // is not applicable, and saying anything else would be misleading.
        if (PatchAcceptance.IsDeclaredSave(request.DeclaredTypes))
        {
            return new PatchStatusReport
            {
                Status = PatchStatus.NotApplicable,
                Evidence = PatchEvidenceClass.Fact,
                EvidenceScope = PatchEvidenceScope.PackageMetadata,
                EvidenceSource = "package metadata (type declares 'save')",
                Explanation = "The package is declared as a save-data resource. Overlay installation does not apply; use save management.",
                GameRoot = request.GameRoot
            };
        }

        var gameRoot = LongPath.Canonical(request.GameRoot);
        if (!Directory.Exists(LongPath.Ensure(gameRoot)))
        {
            return new PatchStatusReport
            {
                Status = PatchStatus.Unknown,
                Evidence = PatchEvidenceClass.None,
                EvidenceScope = PatchEvidenceScope.None,
                Explanation = $"The game directory '{LongPath.Strip(gameRoot)}' does not exist, so nothing can be verified.",
                GameRoot = gameRoot
            };
        }

        var installs = _ledger.ReadAllInstalls(gameRoot, request.GameId);
        var notes = new List<string>();

        PatchManifest? target;
        if (!string.IsNullOrEmpty(request.InstallId))
        {
            target = installs.FirstOrDefault(m => string.Equals(m.InstallId, request.InstallId, StringComparison.Ordinal));
            if (target is null)
            {
                return new PatchStatusReport
                {
                    Status = PatchStatus.Unknown,
                    Evidence = PatchEvidenceClass.None,
                    EvidenceScope = PatchEvidenceScope.GalboxLedger,
                    EvidenceSource = _ledger.GameFolder(request.GameId),
                    Explanation = $"Galbox has no manifest with install id '{request.InstallId}' for this game.",
                    GameRoot = gameRoot
                };
            }
        }
        else
        {
            target = installs.LastOrDefault(m => m.IsActive);
        }

        if (target is null)
        {
            var rolledBack = installs.LastOrDefault(m => m.Status == PatchManifestStatus.RolledBack);
            if (rolledBack is not null)
            {
                return new PatchStatusReport
                {
                    Status = PatchStatus.RolledBack,
                    Evidence = PatchEvidenceClass.Fact,
                    EvidenceScope = PatchEvidenceScope.GalboxLedger,
                    EvidenceSource = _ledger.ManifestPath(rolledBack.GameId, rolledBack.InstallId),
                    Explanation = "Galbox recorded an install for this game and it was rolled back; the game directory was restored from the backups.",
                    GameRoot = gameRoot,
                    InstallId = rolledBack.InstallId,
                    PatchName = rolledBack.PatchName,
                    InstalledAt = rolledBack.CommittedAt ?? rolledBack.CreatedAt,
                    ManagedInstallCount = installs.Count
                };
            }

            return await InferFromTracesAsync(request, gameRoot, ct).ConfigureAwait(false);
        }

        // --- Layer 1: the ledger plus a hash re-check of every file it wrote -------------------------
        var verifications = new List<PatchFileVerification>(target.Files.Count);
        foreach (var file in target.Files)
        {
            ct.ThrowIfCancellationRequested();
            var full = LongPath.CombineUnder(gameRoot, file.Rel);
            var extended = LongPath.Ensure(full);

            if (!File.Exists(extended))
            {
                verifications.Add(new PatchFileVerification
                {
                    RelativePath = file.Rel,
                    ExpectedSha256 = file.HashAfter,
                    ExpectedSizeBytes = file.SizeAfter,
                    State = PatchFileVerificationState.Missing,
                    Note = "The file recorded by the manifest is gone."
                });
                continue;
            }

            string? actual;
            try
            {
                actual = await PatchHashing.TryHashFileAsync(full, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                verifications.Add(new PatchFileVerification
                {
                    RelativePath = file.Rel,
                    ExpectedSha256 = file.HashAfter,
                    ExpectedSizeBytes = file.SizeAfter,
                    State = PatchFileVerificationState.Unreadable,
                    Note = ex.Message
                });
                continue;
            }

            long? actualSize = null;
            try
            {
                actualSize = new FileInfo(extended).Length;
            }
            catch (IOException)
            {
            }

            verifications.Add(new PatchFileVerification
            {
                RelativePath = file.Rel,
                ExpectedSha256 = file.HashAfter,
                ActualSha256 = actual,
                ExpectedSizeBytes = file.SizeAfter,
                ActualSizeBytes = actualSize,
                State = string.Equals(actual, file.HashAfter, StringComparison.OrdinalIgnoreCase)
                    ? PatchFileVerificationState.Match
                    : PatchFileVerificationState.HashMismatch
            });
        }

        var changed = verifications.Where(v => v.State != PatchFileVerificationState.Match).ToList();

        if (installs.Count > 1)
        {
            notes.Add($"{installs.Count} installs are recorded for this game; this verdict is about the newest active one ({target.InstallId}).");
        }
        if (target.Status == PatchManifestStatus.Failed)
        {
            notes.Add("The journal for this install recorded a failure; a rollback is recommended.");
        }

        var manifestPath = _ledger.ManifestPath(target.GameId, target.InstallId);

        if (target.Status == PatchManifestStatus.RolledBack)
        {
            return Assert(new PatchStatusReport
            {
                Status = PatchStatus.RolledBack,
                Evidence = PatchEvidenceClass.Fact,
                EvidenceScope = PatchEvidenceScope.GalboxLedger,
                EvidenceSource = manifestPath,
                Explanation = $"Galbox recorded this install ({target.InstallId}) and it was rolled back on " +
                              $"{target.RolledBackAt:u}; the game directory was restored from the backups, which were kept.",
                GameRoot = gameRoot,
                InstallId = target.InstallId,
                PatchName = target.PatchName,
                InstalledAt = target.CommittedAt ?? target.CreatedAt,
                ManagedInstallCount = installs.Count,
                Files = verifications,
                ChangedFiles = changed,
                Notes = notes
            });
        }

        if (target.Status is PatchManifestStatus.Pending or PatchManifestStatus.Failed)
        {
            var matching = verifications.Count(v => v.State == PatchFileVerificationState.Match);
            return Assert(new PatchStatusReport
            {
                Status = PatchStatus.Interrupted,
                Evidence = PatchEvidenceClass.Fact,
                EvidenceScope = PatchEvidenceScope.GalboxLedger,
                EvidenceSource = manifestPath,
                Explanation = target.Status == PatchManifestStatus.Pending
                    ? $"The install started at {target.CreatedAt:u} and never committed: it was interrupted. " +
                      $"{matching}/{verifications.Count} files already match what the install intended to write."
                    : $"The install recorded a failure. {matching}/{verifications.Count} files match what the install intended to write.",
                GameRoot = gameRoot,
                InstallId = target.InstallId,
                PatchName = target.PatchName,
                InstalledAt = target.CreatedAt,
                ManagedInstallCount = installs.Count,
                Files = verifications,
                ChangedFiles = changed,
                Notes = notes
            });
        }

        if (changed.Count == 0)
        {
            return Assert(new PatchStatusReport
            {
                Status = PatchStatus.Installed,
                Evidence = PatchEvidenceClass.Fact,
                EvidenceScope = PatchEvidenceScope.GalboxLedger,
                EvidenceSource = manifestPath,
                Explanation = $"Installed by Galbox on {target.CommittedAt ?? target.CreatedAt:u}: all {verifications.Count} recorded file(s) still match the hashes written at install time.",
                GameRoot = gameRoot,
                InstallId = target.InstallId,
                PatchName = target.PatchName,
                InstalledAt = target.CommittedAt ?? target.CreatedAt,
                ManagedInstallCount = installs.Count,
                Files = verifications,
                Notes = notes
            });
        }

        notes.Add($"{changed.Count} file(s) no longer match: the game was updated, another patch overwrote them, or antivirus intervened.");

        return Assert(new PatchStatusReport
        {
            Status = PatchStatus.ModifiedSinceInstall,
            Evidence = PatchEvidenceClass.Fact,
            EvidenceScope = PatchEvidenceScope.GalboxLedger,
            EvidenceSource = manifestPath,
            Explanation = $"Galbox installed this patch on {target.CommittedAt ?? target.CreatedAt:u}, but {changed.Count} of {verifications.Count} recorded file(s) have changed since. " +
                          "The patch is still recorded as installed; it has simply been modified afterwards.",
            GameRoot = gameRoot,
            InstallId = target.InstallId,
            PatchName = target.PatchName,
            InstalledAt = target.CommittedAt ?? target.CreatedAt,
            ManagedInstallCount = installs.Count,
            Files = verifications,
            ChangedFiles = changed,
            Notes = notes
        });
    }

    private async Task<PatchStatusReport> InferFromTracesAsync(PatchStatusRequest request, string gameRoot, CancellationToken ct)
    {
        if (!request.ScanHeuristics)
        {
            return new PatchStatusReport
            {
                Status = PatchStatus.NotInstalled,
                Evidence = PatchEvidenceClass.Fact,
                EvidenceScope = PatchEvidenceScope.GalboxLedger,
                EvidenceSource = _ledger.GameFolder(request.GameId),
                Explanation = "Galbox has no record of installing a patch into this game directory. " +
                              "This is a fact about Galbox's own ledger only - it says nothing about patches applied by other tools.",
                GameRoot = gameRoot
            };
        }

        var traces = await Task.Run(() => _scanner.Scan(gameRoot, request.MaxTraces, ct), ct).ConfigureAwait(false);

        if (traces.Count > 0)
        {
            var examples = string.Join(", ", traces.Take(5).Select(t => t.RelativePath));
            return Assert(new PatchStatusReport
            {
                Status = PatchStatus.Suspected,
                Evidence = PatchEvidenceClass.Inference,
                EvidenceScope = PatchEvidenceScope.GameDirectoryHeuristics,
                EvidenceSource = "game directory scan",
                Explanation = $"No Galbox ledger entry exists for this game, so Galbox cannot know whether a patch is installed. " +
                              $"It did find {traces.Count} third-party trace(s) ({examples}), which suggests something was patched by another tool. " +
                              "This is an inference only: file traces cannot tell which patch, which version, or whether it is complete.",
                GameRoot = gameRoot,
                Traces = traces,
                Notes = new[]
                {
                    "Heuristics never produce an 'installed' verdict. Only Galbox's own manifest plus a hash re-check can do that."
                }
            });
        }

        return Assert(new PatchStatusReport
        {
            Status = PatchStatus.NotInstalled,
            Evidence = PatchEvidenceClass.Fact,
            EvidenceScope = PatchEvidenceScope.GalboxLedger,
            EvidenceSource = _ledger.GameFolder(request.GameId),
            Explanation = "Galbox has no ledger entry for this game and found no known third-party patch traces. " +
                          "Within Galbox's ledger this means 'not installed'; it is not proof that the game was never patched by another tool.",
            GameRoot = gameRoot,
            Notes = new[] { "Scope: Galbox's own ledger and a bounded heuristic scan of the game directory." }
        });
    }

    /// <summary>
    /// The firewall between fact and inference. A report that claims a fact-only verdict with non-factual
    /// evidence is a bug, and this throws instead of letting it reach the UI as a confident lie.
    /// </summary>
    private static PatchStatusReport Assert(PatchStatusReport report)
    {
        if (!PatchStatusRules.IsCompatible(report.Status, report.Evidence))
        {
            throw new InvalidOperationException(
                $"Internal invariant violated: status '{report.Status}' cannot be reported with evidence '{report.Evidence}'.");
        }
        return report;
    }
}
