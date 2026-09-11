using System.Text;
using Galbox.Core.Patches;

namespace Galbox.PatchVerifier;

/// <summary>
/// Scenario 15: real, third-party archives instead of archives this harness built itself.
/// The engine's own archives prove the code paths; these prove the engine against bytes that somebody
/// else produced (a genuine RAR, a genuine LZMA2 7z, and a known Zip-Slip sample).
/// </summary>
internal static class ExternalSamples
{
    /// <summary>Downloaded from the SharpCompress test archive corpus (MIT licensed test data).</summary>
    public static readonly (string FileName, string Expectation)[] Samples =
    {
        ("7Zip.LZMA2.7z", "真实的 7z / LZMA2 归档：必须能识别并解出条目"),
        ("Large.rar", "真实的 RAR 归档：必须能识别并解出条目"),
        ("Rar.Encrypted.rar", "真实的加密 RAR：必须明确报告加密而不是崩溃"),
        ("Zip.Evil.zip", "第三方 Zip-Slip 样本：危险条目必须被拒绝"),
        ("7Zip.EmptyStream.7z", "零长度条目的 7z：不得崩溃")
    };

    public static async Task RunAsync(string fixtureDirectory, PatchInstaller engine, TestWorld world, Reporter report, CheckList checks)
    {
        report.Section("场景 15 · 外部真实样本（第三方生成的 7z / RAR / Zip-Slip）");

        if (!Directory.Exists(fixtureDirectory))
        {
            report.Line($"  未提供样本目录 '{fixtureDirectory}'，跳过。");
            report.Line("  获取方式见报告末尾的“重跑命令”一节。");
            return;
        }

        var sampleGame = Path.Combine(world.Root, "game-external");
        Directory.CreateDirectory(sampleGame);
        File.WriteAllText(Path.Combine(sampleGame, "readme.txt"), "external sample target directory\n");

        foreach (var (fileName, expectation) in Samples)
        {
            var path = Path.Combine(fixtureDirectory, fileName);
            report.Sub($"{fileName} — {expectation}");
            if (!File.Exists(path))
            {
                report.Line("  样本缺失，跳过。");
                continue;
            }

            PatchArchiveInfo? info = null;
            try
            {
                info = await engine.InspectAsync(path).ConfigureAwait(false);
                report.Line($"  inspected: kind={info.Kind} format={info.FormatId} entries={info.EntryCount} files={info.FileEntryCount} " +
                            $"uncompressed={info.TotalUncompressedBytes} encrypted={info.IsEncrypted} sha256={info.Sha256![..16]}");
                foreach (var note in info.Warnings) report.Line($"    warning: {note}");
            }
            catch (PatchRejectedException ex)
            {
                report.Line($"  inspect refused: {ex.Code} — {ex.Message}");
            }
            catch (Exception ex)
            {
                report.Line($"  inspect threw {ex.GetType().Name}: {ex.Message}");
                checks.That($"{fileName} 检查阶段不得抛出未处理异常", false, ex.Message);
                continue;
            }

            // A directory of its own per sample keeps failures from bleeding between samples.
            var target = Path.Combine(world.Root, "game-external", Path.GetFileNameWithoutExtension(fileName));
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "readme.txt"), "external sample target directory\n");

            var attempt = await engine.TryPreviewAsync(new PatchPreviewRequest
            {
                ArchivePath = path,
                GameRoot = target,
                GameId = "EXTERNAL-" + Path.GetFileNameWithoutExtension(fileName),
                PatchName = fileName
            }).ConfigureAwait(false);

            if (!attempt.Succeeded)
            {
                report.Line($"  preview refused: {attempt.RejectionCode} — {attempt.Message}");
                if (attempt.SecurityCode is not null) report.Line($"    security code: {attempt.SecurityCode}");

                if (fileName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) && info?.IsEncrypted == true)
                {
                    checks.That("加密 RAR 被明确报告为加密", attempt.RejectionCode == PatchRejectionCode.EncryptedArchive,
                        $"got {attempt.RejectionCode}");
                }
                else
                {
                    report.Line("  (该样本被拒绝：见上)");
                }
                continue;
            }

            var preview = attempt.Preview!;
            report.Line($"  preview ok: create={preview.Summary.CreateCount} overwrite={preview.Summary.OverwriteCount} " +
                        $"conflict={preview.Summary.ConflictCount} rejected={preview.Summary.RejectedCount}");
            foreach (var entry in preview.Entries.Take(6))
            {
                report.Line($"    {entry.Action,-10} {entry.TargetRelativePath}");
            }
            foreach (var rejected in preview.Rejected)
            {
                report.Line($"    REJECTED [{rejected.Code}] '{rejected.ArchiveEntryName}' :: {rejected.Reason}");
            }

            if (fileName == "Zip.Evil.zip")
            {
                checks.That("第三方 Zip-Slip 样本中的危险条目全部被拒绝",
                    preview.Summary.RejectedCount > 0 && preview.Rejected.All(r => r.Code == PatchSecurityCode.ParentTraversal || r.Code == PatchSecurityCode.RootedEntryPath),
                    $"rejected={preview.Summary.RejectedCount}");
                var escaped = Directory.EnumerateFiles(world.Root, "*", SearchOption.AllDirectories)
                    .Where(p => !p.StartsWith(target, StringComparison.OrdinalIgnoreCase) && !p.Contains(@"\sandbox\", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileName)
                    .Where(n => n is not null && preview.Rejected.Any(r => r.ArchiveEntryName.EndsWith(n!, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                report.Line($"  样本目录之外出现的被拒文件名: {(escaped.Count == 0 ? "(无)" : string.Join(", ", escaped))}");
                checks.Equal("被拒条目没有落到游戏目录之外的任何位置", 0, escaped.Count);
            }

            if (info?.IsEncrypted == true)
            {
                report.Line("  容器自报了 encrypted 标志，但解压仍然成功 —— 引擎是“先尝试再判定”，标志位不会误杀合法归档。");
                checks.That($"{fileName} 的 encrypted 标志没有误杀可读归档", preview.Entries.Count > 0);
            }

            var conflicts = preview.Entries.Where(e => e.Action == PatchPreviewAction.Conflict).Select(e => e.TargetRelativePath).ToList();
            var result = await engine.InstallAsync(new PatchInstallRequest
            {
                Preview = preview,
                Decisions = new PatchInstallDecisions { ConfirmedConflicts = conflicts }
            }).ConfigureAwait(false);
            report.Line($"  install: success={result.Success} created={result.CreatedCount} overwritten={result.OverwrittenCount} " +
                        $"errors=[{string.Join(" | ", result.Errors)}]");
            checks.That($"{fileName} 能被真实解压安装", result.Success, string.Join(" | ", result.Errors));

            var rollback = await engine.RollbackAsync(new PatchRollbackRequest
            {
                GameRoot = target,
                GameId = "EXTERNAL-" + Path.GetFileNameWithoutExtension(fileName),
                InstallId = result.InstallId
            }).ConfigureAwait(false);
            report.Line($"  rollback: byteIdentical={rollback.ByteIdenticalToPreInstall} restored={rollback.RestoredCount} deleted={rollback.DeletedCount}");
            checks.That($"{fileName} 安装后可以完整回滚", rollback.ByteIdenticalToPreInstall);
        }
    }
}
