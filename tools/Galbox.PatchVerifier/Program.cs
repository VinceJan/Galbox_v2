using System.Text;
using Galbox.Core.Patches;

namespace Galbox.PatchVerifier;

/// <summary>
/// Evidence generator for the local patch installer.
/// Every claim in the delivery report is produced by this program: nothing here is asserted by hand.
/// </summary>
internal static class Program
{
    private static Reporter _report = null!;
    private static CheckList _checks = null!;
    private static TestWorld _world = null!;

    private static async Task<int> Main(string[] args)
    {
        var scratch = @"E:\tmp\_galbox_patch_scratch";
        string? fixtureDirectory = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--scratch") scratch = args[i + 1];
            if (args[i] == "--fixtures") fixtureDirectory = args[i + 1];
        }

        fixtureDirectory ??= Path.Combine(scratch, "fixtures");

        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (IOException)
        {
            // A redirected console may reject the encoding change; the report file stays UTF-8 regardless.
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        _report = new Reporter(Path.Combine(scratch, "verify-report.txt"));
        _checks = new CheckList();

        try
        {
            _report.Section("Galbox 本机补丁安装器 · 验证报告");
            _report.Line($"生成时间        : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            _report.Line($"运行时          : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} / {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            _report.Line($"SharpCompress   : {typeof(SharpCompress.Archives.ArchiveFactory).Assembly.GetName().Version}");
            _report.Line($"scratch 目录    : {scratch}");
            _report.Line("说明            : 本文件是逐条原始输出。所有断言由工具打印，未人工修饰。");

            _world = TestWorld.Create(scratch, _report.Line);
            _report.Line($"游戏目录        : {_world.GameRoot}");
            _report.Line($"备份根(本场景)  : {_world.BackupRoot}");
            _report.Line($"沙箱根(本场景)  : {_world.SandboxRoot}");
            _report.Line($"默认备份根      : {PatchInstallerOptions.DefaultBackupRoot}");
            _report.Line($"默认沙箱根      : {PatchInstallerOptions.DefaultSandboxRoot}");
            _report.Line($"默认保留次数    : {new PatchInstallerOptions().BackupRetentionCount}");
            _report.Line($"默认放行存档类  : {new PatchInstallerOptions().AllowSaveLikeContent}");
            _checks.That("默认备份根是 %LOCALAPPDATA%\\Galbox\\patchbak",
                PatchInstallerOptions.DefaultBackupRoot.Equals(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galbox", "patchbak"),
                    StringComparison.OrdinalIgnoreCase));
            _checks.That("默认备份根不在游戏目录内",
                !Path.GetFullPath(PatchInstallerOptions.DefaultBackupRoot)
                    .StartsWith(Path.GetFullPath(_world.GameRoot), StringComparison.OrdinalIgnoreCase));

            await Scenario01_BasicFlowAsync().ConfigureAwait(false);
            await Scenario02_ShiftJisAsync().ConfigureAwait(false);
            await Scenario03_ZipSlipAsync().ConfigureAwait(false);
            await Scenario04_AbsolutePathsAsync().ConfigureAwait(false);
            await Scenario05_LongPathAsync().ConfigureAwait(false);
            await Scenario06_HostileNamesAsync().ConfigureAwait(false);
            await Scenario07_SaveRejectionAsync().ConfigureAwait(false);
            await Scenario08_SelfExtractingAsync().ConfigureAwait(false);
            await Scenario09_InterruptedInstallAsync().ConfigureAwait(false);
            Scenario10_StatusHonestyAsync();
            await Scenario13_TarGzAsync().ConfigureAwait(false);
            await Scenario11_StatusLifecycleAsync().ConfigureAwait(false);
            await Scenario12_ManifestBothPlacesAsync().ConfigureAwait(false);
            await Scenario14_RetentionAsync().ConfigureAwait(false);
            await ExternalSamples.RunAsync(fixtureDirectory, NewEngine(_report.Line), _world, _report, _checks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _report.Line();
            _report.Line($"[FATAL] 未捕获异常: {ex}");
            _checks.That("验证过程中未出现未捕获异常", false, ex.Message);
        }

        _report.Section("验证结论");
        _report.Line($"通过: {_checks.Passed} 项断言");
        _report.Line($"失败: {_checks.Failures.Count} 项");
        foreach (var failure in _checks.Failures)
        {
            _report.Line($"  [FAIL] {failure}");
        }
        _report.Line(_checks.AllPassed ? "=> 全部通过 (ALL CHECKS PASSED)" : "=> 存在失败项 (CHECKS FAILED)");
        _report.Line();
        _report.Line($"报告文件: {Path.Combine(scratch, "verify-report.txt")}");

        _report.Dispose();
        return _checks.AllPassed ? 0 : 1;
    }

    /// <summary>
    /// Paths excluded from "the game directory state" snapshots. The engine's own ledger copy lives inside
    /// the game directory on purpose and is rewritten by every install and rollback, so comparing it would
    /// only measure Galbox's own bookkeeping. Everything else must match byte for byte.
    /// </summary>
    private static readonly string[] BookkeepingExclusions = { @".galbox\" };

    private static DirectorySnapshot Snapshot() => DirectorySnapshot.Capture(_world.GameRoot, BookkeepingExclusions);

    private static PatchInstaller NewEngine(Action<string>? log = null)
    {
        var options = new PatchInstallerOptions
        {
            BackupRoot = _world.BackupRoot,
            SandboxRoot = _world.SandboxRoot,
            BackupRetentionCount = 5
        }.WithLog(log ?? (_ => { }));
        return new PatchInstaller(options);
    }

    private static IReadOnlyDictionary<string, string> KnownOriginals(params string[] relativePaths)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);        foreach (var relative in relativePaths)
        {
            if (_world.OriginalGameFiles.TryGetValue(relative, out var content))
            {
                map[relative] = TestWorld.Hash(content);
            }
        }
        return map;
    }

    private static async Task<PatchInstallResult> InstallAsync(
        PatchInstaller engine,
        string archiveFile,
        string gameRoot,
        string gameId,
        string patchName,
        IReadOnlyDictionary<string, string>? knownOriginals = null,
        IReadOnlyList<string>? declaredTypes = null,
        string? subDirectory = null,
        bool confirmConflicts = true)
    {
        var preview = await engine.PreviewAsync(new PatchPreviewRequest
        {
            ArchivePath = archiveFile,
            GameRoot = gameRoot,
            GameId = gameId,
            PatchName = patchName,
            DeclaredTypes = declaredTypes ?? Array.Empty<string>(),
            KnownOriginalHashes = knownOriginals
        }).ConfigureAwait(false);

        var conflicts = preview.Entries.Where(e => e.Action == PatchPreviewAction.Conflict).Select(e => e.TargetRelativePath).ToList();

        return await engine.InstallAsync(new PatchInstallRequest
        {
            Preview = preview,
            Decisions = new PatchInstallDecisions
            {
                ConfirmedConflicts = confirmConflicts ? conflicts : Array.Empty<string>()
            }
        }).ConfigureAwait(false);
    }

    private static void PrintPreview(OverwritePreview preview)
    {
        _report.Line($"  previewId        : {preview.PreviewId}");
        _report.Line($"  archive          : {preview.Archive.FileName}  ({preview.Archive.SizeBytes} B, format={preview.Archive.FormatId}, kind={preview.Archive.Kind})");
        _report.Line($"  archive sha256   : {preview.Archive.Sha256}");
        _report.Line($"  name encoding    : requested={PatchNameEncodings.ToId(preview.Archive.RequestedNameEncoding)} effective={PatchNameEncodings.ToId(preview.Archive.EffectiveNameEncoding)} auto={preview.Archive.NameEncodingWasAutoDetected}");
        _report.Line($"  gameRoot         : {preview.GameRoot}");
        _report.Line($"  sandbox          : {preview.SandboxRoot}");
        _report.Line($"  summary          : create={preview.Summary.CreateCount} overwrite={preview.Summary.OverwriteCount} conflict={preview.Summary.ConflictCount} unchanged={preview.Summary.UnchangedCount} rejected={preview.Summary.RejectedCount}");
        _report.Line($"  bytesToWrite     : {preview.Summary.BytesToWrite}   bytesToBackup={preview.Summary.BytesToBackup}");
        _report.Line();
        _report.Line($"  {"#",-3} {"action",-10} {"provenance",-16} {"target (final landing spot)",-52} {"incoming",-16} {"existing",-16} reason");
        _report.Line($"  {new string('-', 3)} {new string('-', 10)} {new string('-', 16)} {new string('-', 52)} {new string('-', 16)} {new string('-', 16)} {new string('-', 40)}");

        foreach (var entry in preview.Entries)
        {
            var target = entry.TargetRelativePath;
            if (target.Length > 50) target = "..." + target[^49..];
            _report.Line($"  {entry.ArchiveEntryIndex,-3} {entry.Action,-10} {entry.Provenance,-16} {target,-52} " +
                         $"{entry.IncomingSizeBytes + " B",-16} {(entry.ExistingSizeBytes is null ? "-" : entry.ExistingSizeBytes + " B"),-16} {entry.AssessmentReason}");
            if (entry.NameSanitized)
            {
                _report.Line($"      ^ name normalised: '{entry.ArchiveEntryName}' -> '{entry.TargetRelativePath}'  ({entry.SanitizationNote})");
            }
        }

        foreach (var rejected in preview.Rejected)
        {
            _report.Line();
            _report.Line($"  REJECTED [{rejected.Code}] entry#{rejected.ArchiveEntryIndex} '{rejected.ArchiveEntryName}'");
            _report.Line($"      reason: {rejected.Reason}");
        }

        if (preview.Warnings.Count > 0)
        {
            _report.Line();
            foreach (var warning in preview.Warnings)
            {
                _report.Line($"  warning: {warning}");
            }
        }
    }

    private static void PrintBackupTree(string backupRoot, string gameId)
    {
        var folder = Path.Combine(backupRoot, gameId);
        _report.Line($"  backup root: {folder}");
        if (!Directory.Exists(folder))
        {
            _report.Line("    (empty)");
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(folder))
        {
            _report.Line($"    {Path.GetFileName(directory)}\\");
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                _report.Line($"      {Path.GetRelativePath(directory, path),-56} {new FileInfo(path).Length,8} B  {DirectorySnapshot.Hash(path)}");
            }
        }
    }

    private static async Task Scenario01_BasicFlowAsync()
    {
        _report.Section("场景 1 · 主流程：预览 / 只备份被覆盖文件 / 安装 / 回滚逐字节一致");
        var engine = NewEngine(_report.Line);
        var gameRoot = _world.GameRoot;
        var before = Snapshot();

        _report.Sub("1.1 安装前游戏目录快照（哈希基线）");
        before.Print(_report.Line);
        _report.Line($"  文件数={before.Files.Count} 总字节={before.TotalBytes}");

        _report.Sub("1.2 覆盖预览（每个条目的最终落点）");
        var preview = await engine.PreviewAsync(new PatchPreviewRequest
        {
            ArchivePath = _world.Archive("patch-normal.zip"),
            GameRoot = gameRoot,
            GameId = TestWorld.GameId,
            PatchName = "验证用汉化补丁 v1.0",
            DeclaredTypes = new[] { "manual" },
            Languages = new[] { "zh-Hans" },
            Platforms = new[] { "windows" },
            Source = new PatchSourceInfo
            {
                Kind = "nextmoe-moyu",
                PatchId = "300",
                ResourceId = "6165",
                WebUrl = "https://www.moyu.moe/patch/300/introduction",
                ResourceUpdatedAt = "2025-11-11T06:27:34Z"
            },
            KnownOriginalHashes = KnownOriginals("data.xp3", "readme.txt")
        }).ConfigureAwait(false);
        PrintPreview(preview);

        _checks.Equal("预览：新增条目数", 2, preview.Summary.CreateCount);
        _checks.Equal("预览：覆盖条目数（KnownOriginal 判定）", 2, preview.Summary.OverwriteCount);
        _checks.Equal("预览：冲突条目数（来历不明需确认）", 1, preview.Summary.ConflictCount);
        _checks.Equal("预览：未变化条目数", 1, preview.Summary.UnchangedCount);
        _checks.Equal("预览：被拒绝条目数", 0, preview.Summary.RejectedCount);
        _checks.That("预览：冲突条目就是未登记的 sub\\config.ini",
            preview.Entries.Single(e => e.Action == PatchPreviewAction.Conflict).TargetRelativePath == @"sub\config.ini");
        _checks.That("预览：确认清单落到目标路径（未自动剥离顶层目录）",
            preview.Entries.Any(e => e.TargetRelativePath == @"sub\extra\new.dat"));
        _checks.That("预览：必须要求用户确认", preview.RequiresConfirmation);

        _report.Sub("1.3 冲突未确认时安装被阻止");
        var blocked = await engine.InstallAsync(new PatchInstallRequest
        {
            Preview = preview,
            Decisions = PatchInstallDecisions.None
        }).ConfigureAwait(false);
        _report.Line($"  success={blocked.Success} blockingConflicts=[{string.Join(", ", blocked.BlockingConflicts)}]");
        _report.Line($"  errors: {string.Join(" | ", blocked.Errors)}");
        _checks.That("未确认冲突时安装被拒绝", !blocked.Success && blocked.BlockingConflicts.Count == 1);
        _checks.That("被阻止的安装没有改动任何文件", Snapshot().IsIdenticalTo(before));

        _report.Sub("1.4 用户确认冲突后安装");
        var confirmed = preview.Entries.Where(e => e.Action == PatchPreviewAction.Conflict).Select(e => e.TargetRelativePath).ToList();
        var result = await engine.InstallAsync(new PatchInstallRequest
        {
            Preview = preview,
            Decisions = new PatchInstallDecisions { ConfirmedConflicts = confirmed }
        }).ConfigureAwait(false);

        _report.Line($"  success={result.Success} installId={result.InstallId}");
        _report.Line($"  created={result.CreatedCount} overwritten={result.OverwrittenCount} bytesWritten={result.BytesWritten} bytesBackedUp={result.BytesBackedUp}");
        _report.Line($"  manifest(台账)   : {result.ManifestPath}");
        _report.Line($"  manifest(游戏内) : {result.InGameManifestPath}");
        foreach (var operation in result.Operations)
        {
            _report.Line($"    {operation.Status,-16} {operation.Action,-10} {operation.RelativePath,-40} backup={operation.BackupRel ?? "-"} hashAfter={operation.HashAfter}");
        }
        foreach (var warning in result.Warnings)
        {
            _report.Line($"    warning: {warning}");
        }

        _checks.That("安装成功", result.Success);
        _checks.Equal("安装：新增文件数", 2, result.CreatedCount);
        _checks.Equal("安装：覆盖文件数", 3, result.OverwrittenCount);
        _checks.That("安装：所有文件读回校验通过", result.Operations.All(o => o.Status is PatchFileOperationStatus.Ok or PatchFileOperationStatus.Skipped));

        _report.Sub("1.5 备份目录树（只应包含将被覆盖的 3 个文件）");
        PrintBackupTree(_world.BackupRoot, TestWorld.GameId);
        var backupFiles = Directory.EnumerateFiles(Path.Combine(_world.BackupRoot, TestWorld.GameId), "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFileName(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        _report.Line($"  备份文件: {string.Join(", ", backupFiles)}");
        _checks.Equal("只有被覆盖的文件进了备份", 3, backupFiles.Count);
        _checks.That("新增文件 patch.xp3 未被备份", !backupFiles.Any(f => f.Equals("patch.xp3", StringComparison.OrdinalIgnoreCase)));
        _checks.That("新增文件 new.dat 未被备份", !backupFiles.Any(f => f.Equals("new.dat", StringComparison.OrdinalIgnoreCase)));
        _checks.That("未变化文件 big.bin 未被备份", !backupFiles.Any(f => f.Equals("big.bin", StringComparison.OrdinalIgnoreCase)));
        _checks.That("被覆盖的 data.xp3 已备份", backupFiles.Any(f => f.Equals("data.xp3", StringComparison.OrdinalIgnoreCase)));
        _checks.That("被覆盖的 config.ini 已备份", backupFiles.Any(f => f.Equals("config.ini", StringComparison.OrdinalIgnoreCase)));
        _checks.That("备份不在游戏目录内",
            !Path.GetFullPath(_world.BackupRoot).StartsWith(Path.GetFullPath(gameRoot), StringComparison.OrdinalIgnoreCase));

        _report.Sub("1.6 安装后目录状态与预期哈希对照");
        var after = Snapshot();
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _world.OriginalGameFiles) expected[file.Key] = TestWorld.Hash(file.Value);
        expected["patch.xp3"] = TestWorld.Hash(TestWorld.Deterministic(48_000, 0x44));
        expected["data.xp3"] = TestWorld.Hash(TestWorld.Deterministic(210_000, 0x55));
        expected["readme.txt"] = TestWorld.Hash(Encoding.UTF8.GetBytes("Chinese localisation applied by the verifier.\n"));
        expected[@"sub\extra\new.dat"] = TestWorld.Hash(TestWorld.Deterministic(9_000, 0x66));
        expected[@"sub\config.ini"] = TestWorld.Hash(Encoding.UTF8.GetBytes("[config]\nlanguage=zh-Hans\nwindowed=1\n"));

        foreach (var file in after.Files)
        {
            var wanted = expected.TryGetValue(file.RelativePath, out var value) ? value : "(unexpected)";
            var ok = string.Equals(wanted, file.Sha256, StringComparison.OrdinalIgnoreCase);
            _report.Line($"  {(ok ? "OK  " : "DIFF")} {file.RelativePath,-46} {file.Sha256}");
            if (!ok) _report.Line($"       expected {wanted}");
        }

        _checks.Equal("安装后文件总数", expected.Count, after.Files.Count);
        foreach (var pair in expected)
        {
            var actual = after.Files.FirstOrDefault(f => f.RelativePath.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            _checks.That($"安装后哈希符合预期: {pair.Key}", actual is not null && actual.Sha256 == pair.Value,
                actual is null ? "file missing" : $"{actual.Sha256} != {pair.Value}");
        }

        _report.Sub("1.7 回滚（第一次）");
        var rollback = await engine.RollbackAsync(new PatchRollbackRequest
        {
            GameRoot = gameRoot,
            GameId = TestWorld.GameId,
            InstallId = result.InstallId
        }).ConfigureAwait(false);

        _report.Line($"  success={rollback.Success} byteIdentical={rollback.ByteIdenticalToPreInstall}");
        foreach (var file in rollback.Files)
        {
            _report.Line($"    {file.Outcome,-20} {file.RelativePath,-40} expected={file.ExpectedHash ?? "-"} actual={file.ActualHash ?? "-"} {file.Message}");
        }
        _report.Line($"  pruned directories: [{string.Join(", ", rollback.PrunedDirectories)}]");

        var afterRollback = Snapshot();
        var diff = DirectorySnapshot.Diff(before, afterRollback);
        _report.Sub("1.8 回滚前后快照逐条对照（最硬的一条）");
        _report.Line("  ---- 安装前 ----");
        before.Print(_report.Line);
        _report.Line("  ---- 回滚后 ----");
        afterRollback.Print(_report.Line);
        _report.Line($"  差异条目数: {diff.Count}");
        foreach (var line in diff) _report.Line($"    {line}");

        _checks.That("回滚结果自述为逐字节一致", rollback.ByteIdenticalToPreInstall);
        _checks.That("回滚后目录与安装前逐字节一致（全量哈希比对）", afterRollback.IsIdenticalTo(before),
            string.Join("; ", diff.Take(5)));

        _report.Sub("1.9 回滚不删备份 / 可重复回滚");
        var backupCountAfterRollback = Directory.EnumerateFiles(Path.Combine(_world.BackupRoot, TestWorld.GameId), "*", SearchOption.AllDirectories)
            .Count(p => !p.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase));
        _report.Line($"  回滚后备份文件数: {backupCountAfterRollback}");
        _checks.Equal("回滚后备份仍然存在（3 个）", 3, backupCountAfterRollback);

        var second = await engine.RollbackAsync(new PatchRollbackRequest
        {
            GameRoot = gameRoot,
            GameId = TestWorld.GameId,
            InstallId = result.InstallId
        }).ConfigureAwait(false);
        _report.Line($"  第二次回滚: success={second.Success} byteIdentical={second.ByteIdenticalToPreInstall} warnings=[{string.Join(" | ", second.Warnings)}]");
        _checks.That("可重复回滚且仍然一致", second.ByteIdenticalToPreInstall &&
            Snapshot().IsIdenticalTo(before));
    }

    private static async Task Scenario02_ShiftJisAsync()
    {
        _report.Section("场景 2 · Shift-JIS (CP932) 文件名还原");
        var engine = NewEngine(_report.Line);

        _report.Sub("2.1 归档本身的编码事实");
        var inspect = await engine.InspectAsync(_world.Archive("patch-cp932.zip")).ConfigureAwait(false);
        _report.Line($"  format={inspect.FormatId} entries={inspect.EntryCount} files={inspect.FileEntryCount}");
        _report.Line($"  requestedEncoding={PatchNameEncodings.ToId(inspect.RequestedNameEncoding)} effective={PatchNameEncodings.ToId(inspect.EffectiveNameEncoding)} autoDetected={inspect.NameEncodingWasAutoDetected}");
        foreach (var note in inspect.EncodingNotes) _report.Line($"    note: {note}");
        _checks.Equal("自动识别出 CP932", inspect.EffectiveNameEncoding, PatchNameEncoding.Cp932);

        _report.Sub("2.2 预览里解出的文件名（含码点，避免终端编码干扰）");
        var preview = await engine.PreviewAsync(new PatchPreviewRequest
        {
            ArchivePath = _world.Archive("patch-cp932.zip"),
            GameRoot = _world.GameRoot,
            GameId = TestWorld.GameId,
            PatchName = "CP932 文件名用例"
        }).ConfigureAwait(false);

        var names = preview.Entries.Select(e => e.ArchiveEntryName).ToList();
        foreach (var entry in preview.Entries)
        {
            _report.Line($"  archive name : {entry.ArchiveEntryName}");
            _report.Line($"    code points: {string.Join(" ", entry.ArchiveEntryName.EnumerateRunes().Take(24).Select(r => $"U+{r.Value:X4}"))}");
            _report.Line($"    lands at   : {entry.TargetRelativePath}");
        }

        _checks.That("CP932 文件名无 U+FFFD 替换字符", names.All(n => !n.Contains('\uFFFD')));
        _checks.That("解出日语目录名 '日本語'", names.Any(n => n.Contains("日本語", StringComparison.Ordinal)));
        _checks.That("解出含空格的日语文件名 'テスト フォルダ'", names.Any(n => n.Contains("テスト フォルダ", StringComparison.Ordinal)));
        _checks.That("解出日语文件名 'はろー.txt'", names.Any(n => n.Contains("はろー.txt", StringComparison.Ordinal)));
        _checks.That("解出 'セーブデータ'", names.Any(n => n.Contains("セーブデータ", StringComparison.Ordinal)));
        _checks.That("解出 '漢化説明.txt'", names.Any(n => n == "漢化説明.txt"));

        _report.Sub("2.3 安装后磁盘上的真实文件名");
        var before = Snapshot();
        var result = await InstallAsync(engine, _world.Archive("patch-cp932.zip"), _world.GameRoot, TestWorld.GameId, "CP932 文件名用例").ConfigureAwait(false);
        _checks.That("CP932 用例安装成功", result.Success);

        var installed = Snapshot();
        var newFiles = installed.Files.Where(f => before.Files.All(b => !b.RelativePath.Equals(f.RelativePath, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var file in newFiles)
        {
            _report.Line($"  on disk: {file.RelativePath}   ({file.SizeBytes} B)");
            _report.Line($"    code points: {string.Join(" ", file.RelativePath.EnumerateRunes().Take(28).Select(r => $"U+{r.Value:X4}"))}");
        }
        _checks.Equal("落盘文件数", 3, newFiles.Count);
        _checks.That("磁盘上存在 日本語\\テスト フォルダ\\はろー.txt",
            newFiles.Any(f => f.RelativePath.Equals(@"日本語\テスト フォルダ\はろー.txt", StringComparison.Ordinal)));
        _checks.That("磁盘上存在 日本語\\セーブデータ\\おまけ.dat",
            newFiles.Any(f => f.RelativePath.Equals(@"日本語\セーブデータ\おまけ.dat", StringComparison.Ordinal)));
        _checks.That("磁盘上存在 漢化説明.txt", newFiles.Any(f => f.RelativePath.Equals("漢化説明.txt", StringComparison.Ordinal)));

        _report.Sub("2.4 手动切换编码（强制 UTF-8 应产生乱码，证明开关真的生效）");
        var forced = await engine.PreviewAsync(new PatchPreviewRequest
        {
            ArchivePath = _world.Archive("patch-cp932.zip"),
            GameRoot = _world.GameRoot,
            GameId = TestWorld.GameId,
            ArchiveRead = new PatchArchiveReadOptions { NameEncoding = PatchNameEncoding.Utf8, ForceNameEncoding = true }
        }).ConfigureAwait(false);
        foreach (var entry in forced.Entries.Take(3))
        {
            _report.Line($"  强制 UTF-8 解出: '{entry.ArchiveEntryName}' (hasFFFD={entry.ArchiveEntryName.Contains('\uFFFD')})");
        }
        _checks.That("强制 UTF-8 时文件名确实坏掉（说明手动覆盖可用）",
            forced.Entries.Any(e => e.ArchiveEntryName.Contains('\uFFFD')));
        PatchSandbox.Delete(forced.SandboxRoot);

        var undo = await engine.RollbackAsync(new PatchRollbackRequest { GameRoot = _world.GameRoot, GameId = TestWorld.GameId, InstallId = result.InstallId }).ConfigureAwait(false);
        _checks.That("CP932 用例回滚后目录逐字节还原", undo.ByteIdenticalToPreInstall && Snapshot().IsIdenticalTo(before));
    }

    private static async Task Scenario03_ZipSlipAsync()
    {
        _report.Section("场景 3 · Zip Slip（..\\ 路径穿越）必须被拒绝且不落盘");
        var engine = NewEngine(_report.Line);
        var before = Snapshot();

        var preview = await engine.PreviewAsync(new PatchPreviewRequest
        {
            ArchivePath = _world.Archive("patch-zipslip.zip"),
            GameRoot = _world.GameRoot,
            GameId = TestWorld.GameId,
            PatchName = "zip-slip 用例"
        }).ConfigureAwait(false);
        PrintPreview(preview);

        var rejectedPaths = preview.Rejected.Select(r => r.ArchiveEntryName).ToList();
        _report.Line($"  rejected: [{string.Join(", ", rejectedPaths)}]");

        _checks.That("..\\evil.txt 被拒绝", preview.Rejected.Any(r => r.ArchiveEntryName == @"..\evil.txt"));
        _checks.That("..\\..\\evil2.txt 被拒绝", preview.Rejected.Any(r => r.ArchiveEntryName == @"..\..\evil2.txt"));
        _checks.That("..\\evil-dir\\deep.txt 被拒绝", preview.Rejected.Any(r => r.ArchiveEntryName == @"..\evil-dir\deep.txt"));
        _checks.That("拒绝代码为 ParentTraversal",
            preview.Rejected.Where(r => r.ArchiveEntryName.Contains("..")).All(r => r.Code == PatchSecurityCode.ParentTraversal));
        _checks.That("符号链接条目被拒绝（LinkEntry）",
            preview.Rejected.Any(r => r.Code == PatchSecurityCode.LinkEntry));
        _checks.That("良性折叠 sub\\..\\collapsed.txt 被接受为 collapsed.txt",
            preview.Entries.Any(e => e.TargetRelativePath == "collapsed.txt"));
        _checks.Equal("被拒绝条目数", 4, preview.Rejected.Count);

        var result = await engine.InstallAsync(new PatchInstallRequest { Preview = preview }).ConfigureAwait(false);
        _report.Line($"  install success={result.Success} created={result.CreatedCount}");
        _checks.That("合法条目安装成功", result.Success);

        _report.Sub("3.1 落盘取证：穿越目标一律不存在");
        var escapeTargets = new[]
        {
            Path.Combine(_world.Root, "evil.txt"),
            Path.Combine(Path.GetDirectoryName(_world.Root)!, "evil2.txt"),
            Path.Combine(_world.Root, "evil-dir", "deep.txt"),
            Path.Combine(_world.GameRoot, "evil.txt"),
            Path.Combine(_world.GameRoot, "evil-dir", "deep.txt")
        };

        foreach (var target in escapeTargets)
        {
            _report.Line($"  exists({target}) = {File.Exists(target)}");
        }
        _checks.That("穿越目标文件一个都不存在", escapeTargets.All(t => !File.Exists(t)));
        _checks.That("合法文件已写入", File.Exists(Path.Combine(_world.GameRoot, "legit.txt")));
        _checks.That("良性折叠文件落在游戏根", File.Exists(Path.Combine(_world.GameRoot, "collapsed.txt")));

        _report.Sub("3.2 沙箱之外的目录没有被触碰");
        var outside = Directory.EnumerateFiles(_world.Root, "evil*", SearchOption.AllDirectories).ToList();
        _report.Line($"  {_world.Root} 下匹配 evil* 的文件: {(outside.Count == 0 ? "(无)" : string.Join(", ", outside))}");
        _checks.Equal("scratch 根下没有任何 evil* 文件", 0, outside.Count);

        var undo = await engine.RollbackAsync(new PatchRollbackRequest { GameRoot = _world.GameRoot, GameId = TestWorld.GameId, InstallId = result.InstallId }).ConfigureAwait(false);
        _checks.That("zip-slip 用例回滚后目录逐字节还原", undo.ByteIdenticalToPreInstall && Snapshot().IsIdenticalTo(before));
    }

    private static async Task Scenario04_AbsolutePathsAsync()
    {
        _report.Section("场景 4 · 绝对路径 / 盘符 / UNC 条目必须被拒绝");
        var engine = NewEngine(_report.Line);
        var before = Snapshot();
        var canary = @"C:\Windows\Temp\galbox-evil.txt";
        var canaryExistedBefore = File.Exists(canary);

        var preview = await engine.PreviewAsync(new PatchPreviewRequest
        {
            ArchivePath = _world.Archive("patch-absolute.zip"),
            GameRoot = _world.GameRoot,
            GameId = TestWorld.GameId,
            PatchName = "绝对路径用例"
        }).ConfigureAwait(false);
        PrintPreview(preview);

        foreach (var rejected in preview.Rejected)
        {
            _report.Line($"  绝对路径拒绝: [{rejected.Code}] {rejected.ArchiveEntryName}");
        }

        _checks.Equal("被拒绝条目数", 4, preview.Rejected.Count);
        _checks.That("全部标记为 RootedEntryPath",
            preview.Rejected.All(r => r.Code == PatchSecurityCode.RootedEntryPath));
        _checks.That("C:\\Windows\\Temp\\... 被拒绝", preview.Rejected.Any(r => r.ArchiveEntryName.StartsWith(@"C:\Windows\Temp")));
        _checks.That("UNC 路径被拒绝", preview.Rejected.Any(r => r.ArchiveEntryName.StartsWith(@"\\server")));
        _checks.That("盘符 D: 被拒绝", preview.Rejected.Any(r => r.ArchiveEntryName.StartsWith(@"D:\")));
        _checks.That("只接受合法的 ok.txt", preview.Entries.Count == 1 && preview.Entries[0].TargetRelativePath == "ok.txt");

        var result = await engine.InstallAsync(new PatchInstallRequest { Preview = preview }).ConfigureAwait(false);
        _checks.That("合法条目安装成功", result.Success);
        _checks.Equal("C 盘 canary 文件未被创建", canaryExistedBefore, File.Exists(canary));
        _checks.That("ok.txt 已写入", File.Exists(Path.Combine(_world.GameRoot, "ok.txt")));

        var undo = await engine.RollbackAsync(new PatchRollbackRequest { GameRoot = _world.GameRoot, GameId = TestWorld.GameId, InstallId = result.InstallId }).ConfigureAwait(false);
        _checks.That("绝对路径用例回滚后目录逐字节还原", undo.ByteIdenticalToPreInstall && Snapshot().IsIdenticalTo(before));
    }

    private static async Task Scenario05_LongPathAsync()
    {
        _report.Section("场景 5 · 超过 260 字符的长路径");
        var engine = NewEngine(_report.Line);
        var before = Snapshot();

        var preview = await engine.PreviewAsync(new PatchPreviewRequest
        {
            ArchivePath = _world.Archive("patch-longpath.zip"),
            GameRoot = _world.GameRoot,
            GameId = TestWorld.GameId,
            PatchName = "长路径用例"
        }).ConfigureAwait(false);

        foreach (var entry in preview.Entries)
        {
            _report.Line($"  targetRelativePath length = {entry.TargetRelativePath.Length}");
            _report.Line($"  targetFullPath     length = {entry.TargetFullPath.Length}");
            _report.Line($"    {entry.TargetFullPath}");
        }

        var longest = preview.Entries.OrderByDescending(e => e.TargetFullPath.Length).First();
        _checks.That("存在超过 260 字符的落点", longest.TargetFullPath.Length > 260);
        _checks.Equal("长路径条目没有被拒绝", 0, preview.Rejected.Count);

        var result = await engine.InstallAsync(new PatchInstallRequest { Preview = preview }).ConfigureAwait(false);
        _report.Line($"  install success={result.Success} created={result.CreatedCount} errors=[{string.Join(" | ", result.Errors)}]");
        _checks.That("长路径安装成功", result.Success);
        _checks.That("长路径文件确实存在于磁盘", File.Exists(@"\\?\" + longest.TargetFullPath));

        var undo = await engine.RollbackAsync(new PatchRollbackRequest { GameRoot = _world.GameRoot, GameId = TestWorld.GameId, InstallId = result.InstallId }).ConfigureAwait(false);
        _report.Line($"  rollback byteIdentical={undo.ByteIdenticalToPreInstall} pruned={undo.PrunedDirectories.Count}");
        _checks.That("长路径用例回滚后目录逐字节还原", undo.ByteIdenticalToPreInstall && Snapshot().IsIdenticalTo(before));
    }

    private static async Task Scenario06_HostileNamesAsync()
    {
        _report.Section("场景 6 · Windows 保留名与非法字符");
        var engine = NewEngine(_report.Line);
        var before = Snapshot();

        var preview = await engine.PreviewAsync(new PatchPreviewRequest
        {
            ArchivePath = _world.Archive("patch-hostile-names.zip"),
            GameRoot = _world.GameRoot,
            GameId = TestWorld.GameId,
            PatchName = "恶劣文件名用例"
        }).ConfigureAwait(false);
        PrintPreview(preview);

        var mapping = preview.Entries.ToDictionary(e => e.ArchiveEntryName, e => e.TargetRelativePath);
        foreach (var pair in mapping)
        {
            _report.Line($"  映射: '{pair.Key}' -> '{pair.Value}'");
        }

        _checks.That("CON.txt 被改名", mapping.TryGetValue("CON.txt", out var con) && con != "CON.txt");
        _checks.That("aux.dat 被改名", mapping.Any(p => p.Key.EndsWith("aux.dat") && !p.Value.EndsWith(@"\aux.dat")));
        _checks.That("非法字符 <>|? 被替换", mapping.TryGetValue("bad<name>|who?.txt", out var bad) && !bad.Contains('<') && !bad.Contains('?'));
        _checks.That("结尾点被处理", mapping.TryGetValue("trailing-dot.", out var dot) && !dot.EndsWith('.'));
        _checks.That("以保留名 lpt1 作为目录也被改名", mapping.Any(p => p.Value.StartsWith("lpt1_")));
        _checks.That("所有条目都有改名说明", preview.Entries.Where(e => e.NameSanitized).All(e => !string.IsNullOrEmpty(e.SanitizationNote)));

        var result = await engine.InstallAsync(new PatchInstallRequest { Preview = preview }).ConfigureAwait(false);
        _report.Line($"  install success={result.Success} created={result.CreatedCount}");
        _checks.That("安装成功", result.Success);

        _report.Sub("6.1 落盘后的真实文件名");
        var created = Snapshot().Files
            .Where(f => before.Files.All(b => !b.RelativePath.Equals(f.RelativePath, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var file in created) _report.Line($"  {file.RelativePath}");

        _checks.Equal("落盘文件数", 6, created.Count);
        _checks.That("CON_.txt 存在（不是 CON.txt）", created.Any(f => f.RelativePath == "CON_.txt"));
        _checks.That("fine.txt 原样存在", created.Any(f => f.RelativePath == "fine.txt"));

        var undo = await engine.RollbackAsync(new PatchRollbackRequest { GameRoot = _world.GameRoot, GameId = TestWorld.GameId, InstallId = result.InstallId }).ConfigureAwait(false);
        _checks.That("恶劣文件名用例回滚后目录逐字节还原", undo.ByteIdenticalToPreInstall && Snapshot().IsIdenticalTo(before));
    }

    private static async Task Scenario07_SaveRejectionAsync()
    {
        _report.Section("场景 7 · save 类型 / 存档类内容必须被明确拒绝");
        var engine = NewEngine(_report.Line);
        var before = Snapshot();

        _report.Sub("7.1 调用方声明 type=save");
        var declared = await engine.TryPreviewAsync(new PatchPreviewRequest
        {
            ArchivePath = _world.Archive("patch-normal.zip"),
            GameRoot = _world.GameRoot,
            GameId = TestWorld.GameId,
            DeclaredTypes = new[] { "manual", "save" }
        }).ConfigureAwait(false);
        _report.Line($"  succeeded={declared.Succeeded} code={declared.RejectionCode}");
        _report.Line($"  message: {declared.Message}");
        _report.Line($"  remedy : {declared.Remedy}");
        _checks.That("声明 save 直接被拒", !declared.Succeeded && declared.RejectionCode == PatchRejectionCode.SaveTypePatch);
        _checks.That("拒绝理由说明了存档管理", declared.Remedy?.Contains("save manager", StringComparison.OrdinalIgnoreCase) == true);

        _report.Sub("7.2 未声明但与全 CG 存档结构一致");
        var sniffed = await engine.TryPreviewAsync(new PatchPreviewRequest
        {
            ArchivePath = _world.Archive("patch-save.zip"),
            GameRoot = _world.GameRoot,
            GameId = TestWorld.GameId,
            DeclaredTypes = new[] { "other" }
        }).ConfigureAwait(false);
        _report.Line($"  succeeded={sniffed.Succeeded} code={sniffed.RejectionCode}");
        _report.Line($"  message: {sniffed.Message}");
        _checks.That("存档类内容被内容嗅探拒绝", !sniffed.Succeeded && sniffed.RejectionCode == PatchRejectionCode.SaveLikeContent);

        _checks.That("两次拒绝都没有改动游戏目录", Snapshot().IsIdenticalTo(before));

        _report.Sub("7.3 状态查询：save 类型给出 NotApplicable（事实，来源=包元数据）");
        var status = await engine.GetStatusAsync(new PatchStatusRequest
        {
            GameRoot = _world.GameRoot,
            GameId = TestWorld.GameId,
            DeclaredTypes = new[] { "save" }
        }).ConfigureAwait(false);
        _report.Line($"  status={status.Status} evidence={status.Evidence} scope={status.EvidenceScope}");
        _report.Line($"  explanation: {status.Explanation}");
        _checks.Equal("save 类型状态为 NotApplicable", status.Status, PatchStatus.NotApplicable);
        _checks.Equal("证据类别为 Fact", status.Evidence, PatchEvidenceClass.Fact);
        _checks.Equal("证据范围是包元数据", status.EvidenceScope, PatchEvidenceScope.PackageMetadata);
    }

    private static async Task Scenario08_SelfExtractingAsync()
    {
        _report.Section("场景 8 · 自解压 exe：识别但不执行、不解压");
        var engine = NewEngine(_report.Line);
        var sandboxFoldersBefore = Directory.Exists(_world.SandboxRoot) ? Directory.EnumerateDirectories(_world.SandboxRoot).Count() : 0;
        var sfx = _world.Archive("patch-sfx.exe");
        var hashBefore = DirectorySnapshot.Hash(sfx);
        var before = Snapshot();

        var attempt = await engine.TryPreviewAsync(new PatchPreviewRequest
        {
            ArchivePath = sfx,
            GameRoot = _world.GameRoot,
            GameId = TestWorld.GameId
        }).ConfigureAwait(false);

        _report.Line($"  succeeded={attempt.Succeeded} code={attempt.RejectionCode}");
        _report.Line($"  detected kind : {attempt.Archive?.Kind}");
        _report.Line($"  requiresManualRun : {attempt.Archive?.RequiresManualRun}");
        _report.Line($"  message: {attempt.Message}");
        _report.Line($"  advice : {attempt.Archive?.ManualRunAdvice}");

        _checks.Equal("被识别为自解压可执行文件", attempt.Archive?.Kind, PatchArchiveKind.SelfExtractingExecutable);
        _checks.That("需要用户手动在隔离目录运行", attempt.Archive?.RequiresManualRun == true);
        _checks.Equal("拒绝码为 SelfExtractingExecutable", attempt.RejectionCode, PatchRejectionCode.SelfExtractingExecutable);
        _checks.That("提示里包含隔离目录/手动运行", attempt.Archive?.ManualRunAdvice?.Contains("isolated", StringComparison.OrdinalIgnoreCase) == true);
        _checks.Equal("exe 文件本身未被改动", hashBefore, DirectorySnapshot.Hash(sfx));
        _checks.That("游戏目录未被改动", Snapshot().IsIdenticalTo(before));
        var sandboxFolders = Directory.Exists(_world.SandboxRoot) ? Directory.EnumerateDirectories(_world.SandboxRoot).Count() : 0;
        _report.Line($"  沙箱 preview 目录数：处理前 {sandboxFoldersBefore}，处理后 {sandboxFolders}");
        _checks.That("拒绝 SFX 时没有新建任何沙箱", sandboxFolders == sandboxFoldersBefore);
    }

    private static async Task Scenario09_InterruptedInstallAsync()
    {
        _report.Section("场景 9 · 中断恢复：安装中途被打断后 journal 可识别并能还原");
        var engine = NewEngine(_report.Line);
        var before = Snapshot();

        var preview = await engine.PreviewAsync(new PatchPreviewRequest
        {
            ArchivePath = _world.Archive("patch-interrupt.zip"),
            GameRoot = _world.GameRoot,
            GameId = TestWorld.GameId,
            PatchName = "中断用例",
            KnownOriginalHashes = KnownOriginals(@"sub\config.ini")
        }).ConfigureAwait(false);
        PrintPreview(preview);
        _checks.Equal("中断用例：3 新增 + 1 覆盖", 4, preview.Entries.Count);

        var cts = new CancellationTokenSource();
        var progress = new InlineProgress(p =>
        {
            if (p.Phase == "write" && p.Completed >= 1)
            {
                _report.Line($"  >>> 模拟断电/崩溃：在写入阶段第 {p.Completed + 1} 个文件前触发取消 (current={p.CurrentItem})");
                cts.Cancel();
            }
        });

        var interrupted = false;
        try
        {
            await engine.InstallAsync(new PatchInstallRequest { Preview = preview }, progress, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            interrupted = true;
        }

        _checks.That("安装确实被中断", interrupted);

        var during = Snapshot();
        _report.Sub("9.1 中断后的现场");
        var partialDiff = DirectorySnapshot.Diff(before, during);
        _report.Line($"  与安装前的差异（{partialDiff.Count} 条）:");
        foreach (var line in partialDiff) _report.Line($"    {line}");

        _report.Sub("9.2 journal（pending manifest）是否已落盘");
        var ledgerFolder = Path.Combine(_world.BackupRoot, TestWorld.GameId);
        var manifests = Directory.EnumerateFiles(ledgerFolder, "manifest.json", SearchOption.AllDirectories).ToList();
        foreach (var path in manifests)
        {
            var manifest = PatchLedger.TryReadManifest(path);
            _report.Line($"  {path}");
            _report.Line($"    installId={manifest?.InstallId} status={manifest?.Status} files={manifest?.Files.Count} note={manifest?.StatusNote}");
        }
        var pending = manifests.Select(PatchLedger.TryReadManifest).FirstOrDefault(m => m?.Status == PatchManifestStatus.Pending);
        _checks.That("存在 status=pending 的 journal", pending is not null);
        _checks.That("journal 在第一个文件被改动前就已写入（文件清单完整）", pending?.Files.Count == 4);

        _report.Sub("9.3 FindInterruptedAsync 识别");
        var plans = await engine.FindInterruptedAsync(_world.GameRoot, TestWorld.GameId).ConfigureAwait(false);
        foreach (var plan in plans)
        {
            _report.Line($"  installId={plan.InstallId} status={plan.Status} patch={plan.PatchName} started={plan.StartedAt:u}");
            _report.Line($"  reason : {plan.Reason}");
            _report.Line($"  可还原={plan.RestorableFileCount} 可删除={plan.RemovableFileCount} 无法判断={plan.IndeterminateFileCount}");
            foreach (var step in plan.Steps) _report.Line($"    - {step}");
        }
        _checks.Equal("识别出 1 个中断安装", 1, plans.Count);
        _checks.Equal("journal 状态为 Pending", plans[0].Status, PatchManifestStatus.Pending);

        _report.Sub("9.3b 中断后立即查询状态（模拟“下次启动”）");
        var interruptedStatus = await engine.GetStatusAsync(new PatchStatusRequest
        {
            GameRoot = _world.GameRoot,
            GameId = TestWorld.GameId
        }).ConfigureAwait(false);
        _report.Line($"  status={interruptedStatus.Status} evidence={interruptedStatus.Evidence} scope={interruptedStatus.EvidenceScope}");
        _report.Line($"  explanation: {interruptedStatus.Explanation}");
        _report.Line($"  rollbackAvailable={interruptedStatus.RollbackAvailable}");
        _checks.Equal("状态为 Interrupted", PatchStatus.Interrupted, interruptedStatus.Status);
        _checks.Equal("证据为 Fact（journal 是 Galbox 自己的记录）", PatchEvidenceClass.Fact, interruptedStatus.Evidence);
        _checks.That("提供回滚入口", interruptedStatus.RollbackAvailable);

        _report.Sub("9.4 RecoverAsync 执行恢复");
        var recovery = await engine.RecoverAsync(new PatchRecoverRequest { Plan = plans[0], Apply = true }).ConfigureAwait(false);
        _report.Line($"  success={recovery.Success} byteIdentical={recovery.ByteIdenticalToPreInstall} restored={recovery.RestoredCount} deleted={recovery.DeletedCount} skipped={recovery.SkippedCount}");
        foreach (var file in recovery.Files)
        {
            _report.Line($"    {file.Outcome,-20} {file.RelativePath,-30} {file.Message}");
        }

        var afterRecovery = Snapshot();
        var recoveryDiff = DirectorySnapshot.Diff(before, afterRecovery);
        _report.Line($"  恢复后与中断前差异条目数: {recoveryDiff.Count}");
        foreach (var line in recoveryDiff) _report.Line($"    {line}");

        _checks.That("恢复后目录与安装前逐字节一致", afterRecovery.IsIdenticalTo(before), string.Join("; ", recoveryDiff.Take(5)));

        var afterPlans = await engine.FindInterruptedAsync(_world.GameRoot, TestWorld.GameId).ConfigureAwait(false);
        _checks.Equal("恢复后不再报告中断安装", 0, afterPlans.Count);

        _report.Sub("9.5 状态查询应报告 Interrupted → 恢复后应报告 RolledBack");
        var statusAfter = await engine.GetStatusAsync(new PatchStatusRequest { GameRoot = _world.GameRoot, GameId = TestWorld.GameId }).ConfigureAwait(false);
        _report.Line($"  status={statusAfter.Status} evidence={statusAfter.Evidence} scope={statusAfter.EvidenceScope}");
        _report.Line($"  explanation: {statusAfter.Explanation}");
        _checks.Equal("恢复后的状态为 RolledBack", PatchStatus.RolledBack, statusAfter.Status);
        _checks.Equal("证据类别为 Fact", statusAfter.Evidence, PatchEvidenceClass.Fact);

        var statusPending = await engine.GetStatusAsync(new PatchStatusRequest
        {
            GameRoot = _world.GameRoot,
            GameId = TestWorld.GameId,
            InstallId = plans[0].InstallId
        }).ConfigureAwait(false);
        _report.Line($"  指定 installId 查询: status={statusPending.Status}");
        _checks.Equal("按 installId 查询得到 RolledBack", PatchStatus.RolledBack, statusPending.Status);
    }

    private static void Scenario10_StatusHonestyAsync()
    {
        _report.Section("场景 10 · 状态判定：事实与推测必须分开");
        var engine = NewEngine(_report.Line);

        _report.Sub("10.1 全新目录 + 手工放置 patch.xp3（第三方痕迹）→ 只能是“疑似”");
        var traceGame = Path.Combine(_world.Root, "game-suspected");
        Directory.CreateDirectory(traceGame);
        File.WriteAllBytes(Path.Combine(traceGame, "data.xp3"), TestWorld.Deterministic(5000, 0xAA));
        File.WriteAllBytes(Path.Combine(traceGame, "patch.xp3"), TestWorld.Deterministic(3000, 0xBB));
        File.WriteAllText(Path.Combine(traceGame, "汉化说明.txt"), "由某汉化组制作");
        Directory.CreateDirectory(Path.Combine(traceGame, "BepInEx", "plugins"));

        var suspected = engine.GetStatusAsync(new PatchStatusRequest
        {
            GameRoot = traceGame,
            GameId = "TRACE-ONLY"
        }).GetAwaiter().GetResult();

        _report.Line($"  status      = {suspected.Status}");
        _report.Line($"  evidence    = {suspected.Evidence} (scope={suspected.EvidenceScope})");
        _report.Line($"  authoritative = {suspected.IsAuthoritative}");
        _report.Line($"  explanation = {suspected.Explanation}");
        foreach (var trace in suspected.Traces)
        {
            _report.Line($"    trace [{trace.Code}] {trace.RelativePath} :: {trace.Detail}");
        }

        _checks.Equal("只有第三方痕迹时状态为 Suspected", suspected.Status, PatchStatus.Suspected);
        _checks.Equal("证据类别为 Inference", suspected.Evidence, PatchEvidenceClass.Inference);
        _checks.That("绝不显示为已安装", suspected.Status != PatchStatus.Installed);
        _checks.That("不是权威结论", !suspected.IsAuthoritative);
        _checks.That("没有可回滚的对象", !suspected.RollbackAvailable);
        _checks.That("确实抓到了 patch.xp3 痕迹", suspected.Traces.Any(t => t.RelativePath.Contains("patch.xp3")));
        _checks.That("确实抓到了汉化说明.txt 痕迹", suspected.Traces.Any(t => t.RelativePath.Contains("汉化")));

        _report.Sub("10.2 干净且无痕迹的新目录 → NotInstalled（限定 Galbox 台账范围内的事实）");
        var cleanGame = Path.Combine(_world.Root, "game-clean");
        Directory.CreateDirectory(cleanGame);
        File.WriteAllBytes(Path.Combine(cleanGame, "data.xp3"), TestWorld.Deterministic(1000, 0xCC));

        var clean = engine.GetStatusAsync(new PatchStatusRequest { GameRoot = cleanGame, GameId = "CLEAN" }).GetAwaiter().GetResult();
        _report.Line($"  status={clean.Status} evidence={clean.Evidence} scope={clean.EvidenceScope}");
        _report.Line($"  explanation = {clean.Explanation}");
        _checks.Equal("干净目录状态为 NotInstalled", clean.Status, PatchStatus.NotInstalled);
        _checks.Equal("证据范围限定为 Galbox 台账", clean.EvidenceScope, PatchEvidenceScope.GalboxLedger);

        _report.Sub("10.3 事实/推测防火墙自检");
        _report.Line($"  IsCompatible(Installed, Inference)  = {PatchStatusRules.IsCompatible(PatchStatus.Installed, PatchEvidenceClass.Inference)}");
        _report.Line($"  IsCompatible(Installed, Fact)       = {PatchStatusRules.IsCompatible(PatchStatus.Installed, PatchEvidenceClass.Fact)}");
        _report.Line($"  IsCompatible(Suspected, Inference)  = {PatchStatusRules.IsCompatible(PatchStatus.Suspected, PatchEvidenceClass.Inference)}");
        _report.Line($"  IsCompatible(Suspected, Fact)       = {PatchStatusRules.IsCompatible(PatchStatus.Suspected, PatchEvidenceClass.Fact)}");
        _checks.That("推测不能冒充已安装", !PatchStatusRules.IsCompatible(PatchStatus.Installed, PatchEvidenceClass.Inference));
        _checks.That("Suspected 只能用推测证据", PatchStatusRules.IsCompatible(PatchStatus.Suspected, PatchEvidenceClass.Inference)
                                              && !PatchStatusRules.IsCompatible(PatchStatus.Suspected, PatchEvidenceClass.Fact));
        _checks.That("四个事实性状态都要求 Fact 证据",
            PatchStatusRules.FactOnlyStatuses.All(s => PatchStatusRules.IsCompatible(s, PatchEvidenceClass.Fact)
                                                       && !PatchStatusRules.IsCompatible(s, PatchEvidenceClass.Inference)));
    }

    private static async Task Scenario11_StatusLifecycleAsync()
    {
        _report.Section("场景 11 · 已安装 / 已被改动 的生命周期");
        var engine = NewEngine(_report.Line);
        var gameRoot = _world.GameRoot;
        var before = Snapshot();

        var result = await InstallAsync(engine, _world.Archive("retention-1.zip"), gameRoot, TestWorld.GameId, "状态用例").ConfigureAwait(false);
        _checks.That("状态用例安装成功", result.Success);

        var installed = await engine.GetStatusAsync(new PatchStatusRequest { GameRoot = gameRoot, GameId = TestWorld.GameId }).ConfigureAwait(false);
        _report.Line($"  [安装后] status={installed.Status} evidence={installed.Evidence} installId={installed.InstallId}");
        _report.Line($"           explanation = {installed.Explanation}");
        _checks.Equal("安装后为 Installed", installed.Status, PatchStatus.Installed);
        _checks.Equal("证据为 Fact", installed.Evidence, PatchEvidenceClass.Fact);
        _checks.Equal("证据范围是 Galbox 台账", installed.EvidenceScope, PatchEvidenceScope.GalboxLedger);

        _report.Sub("11.1 改动安装后的文件");
        File.WriteAllText(Path.Combine(gameRoot, "retention-1.txt"), "someone else touched this file");
        var modified = await engine.GetStatusAsync(new PatchStatusRequest { GameRoot = gameRoot, GameId = TestWorld.GameId }).ConfigureAwait(false);
        _report.Line($"  [改动后] status={modified.Status} changed={modified.ChangedFiles.Count}/{modified.Files.Count}");
        foreach (var file in modified.ChangedFiles)
        {
            _report.Line($"    {file.State} {file.RelativePath}: expected {file.ExpectedSha256} actual {file.ActualSha256}");
        }
        _report.Line($"           explanation = {modified.Explanation}");
        _checks.Equal("改动后为 ModifiedSinceInstall", modified.Status, PatchStatus.ModifiedSinceInstall);
        _checks.Equal("指出 1 个变化文件", 1, modified.ChangedFiles.Count);
        _checks.Equal("状态仍来自事实证据", modified.Evidence, PatchEvidenceClass.Fact);

        _report.Sub("11.2 删除安装的文件");
        File.Delete(Path.Combine(gameRoot, "retention-1.txt"));
        var missing = await engine.GetStatusAsync(new PatchStatusRequest { GameRoot = gameRoot, GameId = TestWorld.GameId }).ConfigureAwait(false);
        _report.Line($"  [删除后] status={missing.Status} states=[{string.Join(", ", missing.ChangedFiles.Select(f => f.State))}]");
        _checks.Equal("删除后仍报 ModifiedSinceInstall", missing.Status, PatchStatus.ModifiedSinceInstall);
        _checks.That("状态标记为 Missing", missing.ChangedFiles.Any(f => f.State == PatchFileVerificationState.Missing));

        var undo = await engine.RollbackAsync(new PatchRollbackRequest { GameRoot = gameRoot, GameId = TestWorld.GameId, InstallId = result.InstallId }).ConfigureAwait(false);
        _checks.That("回滚后目录逐字节还原", undo.ByteIdenticalToPreInstall && Snapshot().IsIdenticalTo(before));
    }

    private static async Task Scenario12_ManifestBothPlacesAsync()
    {
        _report.Section("场景 12 · manifest 双份落盘 + 换机器可识别");
        var engine = NewEngine(_report.Line);
        var gameRoot = _world.GameRoot;

        var result = await InstallAsync(engine, _world.Archive("retention-2.zip"), gameRoot, TestWorld.GameId, "双份 manifest 用例").ConfigureAwait(false);
        _checks.That("安装成功", result.Success);

        var ledgerManifest = result.ManifestPath!;
        var inGameManifest = result.InGameManifestPath!;
        _report.Line($"  ① 台账 manifest : {ledgerManifest}");
        _report.Line($"     存在={File.Exists(ledgerManifest)}  大小={new FileInfo(ledgerManifest).Length} B");
        _report.Line($"  ② 游戏内 manifest: {inGameManifest}");
        _report.Line($"     存在={File.Exists(inGameManifest)}  大小={new FileInfo(inGameManifest).Length} B");
        _report.Line();
        _report.Line("  ---- 游戏内 manifest 全文（换机器后仅凭它恢复状态）----");
        foreach (var line in File.ReadAllLines(inGameManifest).Take(60)) _report.Line("  " + line);

        _checks.That("台账 manifest 已写入", File.Exists(ledgerManifest));
        _checks.That("游戏内 manifest 已写入", File.Exists(inGameManifest));
        _checks.That("游戏内 manifest 位于游戏目录下且不在游戏目录的备份里",
            Path.GetFullPath(inGameManifest).StartsWith(Path.GetFullPath(gameRoot), StringComparison.OrdinalIgnoreCase));

        var bundle = PatchJson.Deserialize<PatchGameManifestBundle>(File.ReadAllText(inGameManifest));
        _checks.That("游戏内 manifest 是安装清单集合", bundle is not null && bundle.Installs.Count > 0);
        _checks.That("包含本次 installId", bundle!.Installs.Any(m => m.InstallId == result.InstallId));
        _checks.That("记录了来源描述", bundle.Installs.Any(m => m.PatchName == "双份 manifest 用例"));

        _report.Sub("12.1 模拟换机器：只保留游戏目录副本，台账备份根换到别处");
        var machineB = Path.Combine(_world.Root, "machine-b-game");
        CopyDirectory(gameRoot, machineB);
        var engineB = new PatchInstaller(new PatchInstallerOptions
        {
            BackupRoot = Path.Combine(_world.Root, "machine-b-backups"),
            SandboxRoot = Path.Combine(_world.Root, "machine-b-sandbox")
        }.WithLog(_report.Line));

        var statusB = await engineB.GetStatusAsync(new PatchStatusRequest { GameRoot = machineB, GameId = TestWorld.GameId }).ConfigureAwait(false);
        _report.Line($"  status={statusB.Status} evidence={statusB.Evidence} installId={statusB.InstallId} installs={statusB.ManagedInstallCount}");
        _report.Line($"  explanation: {statusB.Explanation}");
        _checks.Equal("换机器后仍由游戏内副本判定为 Installed", statusB.Status, PatchStatus.Installed);
        _checks.Equal("证据为 Fact", statusB.Evidence, PatchEvidenceClass.Fact);

        _report.Sub("12.2 破坏一个文件后，换机器也应报“已被改动”");
        File.WriteAllText(Path.Combine(machineB, "retention-2.txt"), "tampered on machine B");
        var statusBTampered = await engineB.GetStatusAsync(new PatchStatusRequest { GameRoot = machineB, GameId = TestWorld.GameId }).ConfigureAwait(false);
        _report.Line($"  status={statusBTampered.Status} changed={statusBTampered.ChangedFiles.Count}");
        _checks.Equal("报 ModifiedSinceInstall", statusBTampered.Status, PatchStatus.ModifiedSinceInstall);

        var undo = await engine.RollbackAsync(new PatchRollbackRequest { GameRoot = gameRoot, GameId = TestWorld.GameId, InstallId = result.InstallId }).ConfigureAwait(false);
        _report.Line($"  回滚: byteIdentical={undo.ByteIdenticalToPreInstall}");
    }

    private static async Task Scenario13_TarGzAsync()
    {
        _report.Section("场景 13 · tar.gz（SharpCompress 读取路径）");
        var engine = NewEngine(_report.Line);
        var before = Snapshot();

        var inspect = await engine.InspectAsync(_world.Archive("patch-targz.tar.gz")).ConfigureAwait(false);
        _report.Line($"  kind={inspect.Kind} format={inspect.FormatId} entries={inspect.EntryCount} files={inspect.FileEntryCount}");
        _checks.Equal("识别为 tar.gz", inspect.FormatId, "tar.gz");

        var preview = await engine.PreviewAsync(new PatchPreviewRequest
        {
            ArchivePath = _world.Archive("patch-targz.tar.gz"),
            GameRoot = _world.GameRoot,
            GameId = TestWorld.GameId,
            PatchName = "tar.gz 用例"
        }).ConfigureAwait(false);
        PrintPreview(preview);

        _checks.That("tar.gz 里的 ..\\ 条目也被拒绝",
            preview.Rejected.Any(r => r.ArchiveEntryName.Contains("evil-tar.txt") && r.Code == PatchSecurityCode.ParentTraversal));
        _checks.That("tar 内部的良性折叠也被接受为 collapsed-tar.txt",
            preview.Entries.Any(e => e.TargetRelativePath == @"targz\collapsed-tar.txt"));
        _checks.Equal("合法条目数", 3, preview.Entries.Count);

        var result = await engine.InstallAsync(new PatchInstallRequest { Preview = preview }).ConfigureAwait(false);
        _report.Line($"  install success={result.Success} created={result.CreatedCount}");
        _checks.That("tar.gz 安装成功", result.Success);
        _checks.That("targz/file-a.txt 已写入", File.Exists(Path.Combine(_world.GameRoot, "targz", "file-a.txt")));

        var undo = await engine.RollbackAsync(new PatchRollbackRequest { GameRoot = _world.GameRoot, GameId = TestWorld.GameId, InstallId = result.InstallId }).ConfigureAwait(false);
        _checks.That("tar.gz 用例回滚后目录逐字节还原", undo.ByteIdenticalToPreInstall && Snapshot().IsIdenticalTo(before));
    }

    private static async Task Scenario14_RetentionAsync()
    {
        _report.Section("场景 14 · 备份保留策略（默认最近 5 次，且不静默删除）");

        // A dedicated game directory and game id keep the retention window isolated from the other scenarios.
        const string gameId = "TESTGAME-RETENTION";
        var gameRoot = Path.Combine(_world.Root, "game-retention");
        Directory.CreateDirectory(gameRoot);
        var sharedOriginal = "original shared content, only copy is in the backup\n";
        File.WriteAllText(Path.Combine(gameRoot, "shared.txt"), sharedOriginal);
        var knownOriginals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["shared.txt"] = TestWorld.Hash(Encoding.UTF8.GetBytes(sharedOriginal))
        };

        var engine = NewEngine(_report.Line);
        var before = DirectorySnapshot.Capture(gameRoot, BookkeepingExclusions);
        _report.Line($"  独立游戏目录: {gameRoot}");
        before.Print(_report.Line);

        var installIds = new List<string>();
        for (var i = 1; i <= 6; i++)
        {
            var preview = await engine.PreviewAsync(new PatchPreviewRequest
            {
                ArchivePath = _world.Archive($"retention-{i}.zip"),
                GameRoot = gameRoot,
                GameId = gameId,
                PatchName = $"保留策略 #{i}",
                KnownOriginalHashes = i == 1 ? knownOriginals : null
            }).ConfigureAwait(false);
            var conflicts = preview.Entries.Where(e => e.Action == PatchPreviewAction.Conflict).Select(e => e.TargetRelativePath).ToList();
            var result = await engine.InstallAsync(new PatchInstallRequest
            {
                Preview = preview,
                Decisions = new PatchInstallDecisions { ConfirmedConflicts = conflicts }
            }).ConfigureAwait(false);
            _report.Line($"  安装 #{i}: success={result.Success} created={result.CreatedCount} overwritten={result.OverwrittenCount} id={result.InstallId}");
            _checks.That($"第 {i} 次安装成功", result.Success);
            installIds.Add(result.InstallId);
        }

        var folders = Directory.EnumerateDirectories(Path.Combine(_world.BackupRoot, gameId)).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList();
        _report.Line($"  备份目录中的 installId ({folders.Count} 个):");
        foreach (var folder in folders) _report.Line($"    {folder}");

        _report.Sub("14.1 备份树（第 1 次安装备份了它覆盖掉的 shared.txt）");
        PrintBackupTree(_world.BackupRoot, gameId);

        var plan = engine.Backups.PlanRetention(gameRoot, gameId);
        _report.Line($"  保留策略: keep={plan.KeepCount}");
        _report.Line($"  kept ({plan.Kept.Count}): {string.Join(", ", plan.Kept.Select(k => k.InstallId))}");
        _report.Line($"  reclaimable ({plan.Reclaimable.Count}): {string.Join(", ", plan.Reclaimable.Select(k => k.InstallId))}");
        _report.Line($"  reclaimableBytes={plan.ReclaimableBytes} totalBackupBytes={plan.TotalBackupBytes}");
        _report.Line($"  recommendation: {plan.Recommendation}");

        _checks.Equal("保留 5 次", 5, plan.Kept.Count);
        _checks.Equal("可回收 1 次", 1, plan.Reclaimable.Count);
        _checks.That("可回收的是最早的一次", plan.Reclaimable[0].InstallId == installIds[0]);
        _checks.That("策略只给建议，不自动删除",
            Directory.EnumerateDirectories(Path.Combine(_world.BackupRoot, gameId)).Count() == 6);

        _report.Sub("14.2 回滚第 2..6 次（先证明正常路径都能逐字节还原）");
        for (var i = installIds.Count - 1; i >= 1; i--)
        {
            var undo = await engine.RollbackAsync(new PatchRollbackRequest { GameRoot = gameRoot, GameId = gameId, InstallId = installIds[i] }).ConfigureAwait(false);
            _report.Line($"  rollback #{i + 1} {installIds[i]}: byteIdentical={undo.ByteIdenticalToPreInstall} deleted={undo.DeletedCount}");
            _checks.That($"回滚 {i + 1} 号安装后目录逐字节还原", undo.ByteIdenticalToPreInstall);
        }

        _report.Sub("14.3 受保护（中断中）的备份不可被清理");
        var protectedPlan = engine.Backups.PlanRetention(gameRoot, gameId);
        _checks.That("可回收计划里不含任何受保护项", protectedPlan.Reclaimable.All(c => !c.IsProtected));

        _report.Sub("14.4 显式清理最老的一次，然后尝试回滚它");
        engine.Backups.ApplyRetention(gameRoot, gameId, installIds[0]);
        var remaining = Directory.EnumerateDirectories(Path.Combine(_world.BackupRoot, gameId)).Select(Path.GetFileName).ToList();
        _report.Line($"  清理后剩余 {remaining.Count} 个: {string.Join(", ", remaining.OrderBy(x => x, StringComparer.Ordinal))}");
        _checks.Equal("清理后剩 5 个", 5, remaining.Count);
        _checks.That("被清理的备份目录已删除", !remaining.Contains(installIds[0]));

        var noBackupRollback = await engine.RollbackAsync(new PatchRollbackRequest
        {
            GameRoot = gameRoot,
            GameId = gameId,
            InstallId = installIds[0]
        }).ConfigureAwait(false);
        _report.Line($"  rollback #{1} {installIds[0]}: byteIdentical={noBackupRollback.ByteIdenticalToPreInstall}");
        foreach (var error in noBackupRollback.Errors) _report.Line($"    error: {error}");
        _report.Line("  => 备份被删后，这次安装覆盖掉的 shared.txt 无法恢复。这正是引擎从不静默删除备份的理由。");

        _checks.That("备份被删后该次回滚无法完成（如实报告，不假装成功）", !noBackupRollback.ByteIdenticalToPreInstall);
        _checks.That("错误信息指出备份缺失",
            noBackupRollback.Errors.Any(e => e.Contains("missing", StringComparison.OrdinalIgnoreCase)) ||
            noBackupRollback.Files.Any(f => f.Message?.Contains("missing", StringComparison.OrdinalIgnoreCase) == true));
        _checks.That("它新增的 retention-1.txt 仍被删除", !File.Exists(Path.Combine(gameRoot, "retention-1.txt")));

        var final = DirectorySnapshot.Capture(gameRoot, BookkeepingExclusions);
        var diff = DirectorySnapshot.Diff(before, final);
        _report.Line($"  最终差异条目数: {diff.Count}");
        foreach (var line in diff) _report.Line($"    {line}");

        _checks.Equal("唯一残留就是那次无法回滚的改动", 1, diff.Count);
        _checks.That("残留的是被改动后的 shared.txt",
            diff.Count == 1 && diff[0].Contains("CHANGED", StringComparison.Ordinal) && diff[0].Contains("shared.txt", StringComparison.Ordinal));
    }
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    /// <summary>Synchronous progress sink: the cancellation in scenario 9 must fire inside the loop, not on the thread pool.</summary>
    private sealed class InlineProgress : IProgress<PatchProgress>
    {
        private readonly Action<PatchProgress> _handler;

        public InlineProgress(Action<PatchProgress> handler) => _handler = handler;

        public void Report(PatchProgress value) => _handler(value);
    }
}
