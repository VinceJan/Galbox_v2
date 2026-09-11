using System.Text;
using Galbox.Core.Patches;

namespace Galbox.PatchVerifier;

/// <summary>Builds the synthetic world the harness works in: a fake game directory plus hostile archives.</summary>
internal sealed class TestWorld
{
    public required string Root { get; init; }

    public required string GameRoot { get; init; }

    public required string ArchiveRoot { get; init; }

    public required string SandboxRoot { get; init; }

    public required string BackupRoot { get; init; }

    public required string ReportRoot { get; init; }

    /// <summary>Content of every file the fake game ships with (relative path to bytes).</summary>
    public Dictionary<string, byte[]> OriginalGameFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public const string GameId = "TESTGAME-1";

    /// <summary>Creates the whole scratch tree from scratch.</summary>
    public static TestWorld Create(string scratchRoot, Action<string> log)
    {
        var root = Path.Combine(scratchRoot, "run");
        if (Directory.Exists(root))
        {
            log($"cleaning previous run under {root}");
            Directory.Delete(root, recursive: true);
        }

        var world = new TestWorld
        {
            Root = root,
            GameRoot = Path.Combine(root, "game"),
            ArchiveRoot = Path.Combine(root, "archives"),
            SandboxRoot = Path.Combine(root, "sandbox"),
            BackupRoot = Path.Combine(root, "backups"),
            ReportRoot = scratchRoot
        };

        Directory.CreateDirectory(world.GameRoot);
        Directory.CreateDirectory(world.ArchiveRoot);
        Directory.CreateDirectory(world.SandboxRoot);
        Directory.CreateDirectory(world.BackupRoot);

        world.BuildGameDirectory();
        world.BuildArchives();
        return world;
    }

    /// <summary>The fake game directory: two binaries, a text file and a nested folder.</summary>
    public void BuildGameDirectory()
    {
        AddGameFile("data.xp3", Deterministic(200_000, 0x11));
        AddGameFile("game.exe", Deterministic(64_000, 0x22));
        AddGameFile("readme.txt", Encoding.UTF8.GetBytes("Galbox patch verifier sample game.\nThis file is tracked by hash.\n"));
        AddGameFile(@"sub\config.ini", Encoding.UTF8.GetBytes("[config]\nlanguage=ja\nwindowed=1\n"));
        AddGameFile(@"unrelated\big.bin", Deterministic(120_000, 0x33));

        void AddGameFile(string relative, byte[] content)
        {
            var path = Path.Combine(GameRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            OriginalGameFiles[relative] = content;
        }
    }

    private void BuildArchives()
    {
        var cp932 = Encoding.GetEncoding(932);

        // --- 1. The ordinary patch: create / overwrite / conflict / unchanged in one package --------
        TestArchiveWriter.WriteZip(Path.Combine(ArchiveRoot, "patch-normal.zip"), new[]
        {
            new TestArchiveWriter.Entry { Name = "patch.xp3", Content = Deterministic(48_000, 0x44), Method = 8 },
            new TestArchiveWriter.Entry { Name = "data.xp3", Content = Deterministic(210_000, 0x55), Method = 8 },
            new TestArchiveWriter.Entry { Name = "readme.txt", Content = Encoding.UTF8.GetBytes("Chinese localisation applied by the verifier.\n") },
            new TestArchiveWriter.Entry { Name = @"sub\extra\new.dat", Content = Deterministic(9_000, 0x66) },
            new TestArchiveWriter.Entry { Name = @"sub\config.ini", Content = Encoding.UTF8.GetBytes("[config]\nlanguage=zh-Hans\nwindowed=1\n") },
            new TestArchiveWriter.Entry { Name = @"unrelated\big.bin", Content = OriginalGameFiles[@"unrelated\big.bin"] }
        });

        // --- 2. Shift-JIS file names, no UTF-8 flag: exactly how a Japanese packer emits them --------
        TestArchiveWriter.WriteZip(Path.Combine(ArchiveRoot, "patch-cp932.zip"), new[]
        {
            new TestArchiveWriter.Entry { Name = @"日本語\テスト フォルダ\はろー.txt", Content = Encoding.UTF8.GetBytes("cp932 name payload 1") },
            new TestArchiveWriter.Entry { Name = @"日本語\セーブデータ\おまけ.dat", Content = Deterministic(2048, 0x77), Method = 8 },
            new TestArchiveWriter.Entry { Name = "漢化説明.txt", Content = Encoding.UTF8.GetBytes("all three entry names are raw CP932 bytes, the UTF-8 flag stays 0") }
        }, cp932: true);

        // --- 3. Zip Slip: relative traversal + a benign collapse + a symlink entry -------------------
        TestArchiveWriter.WriteZip(Path.Combine(ArchiveRoot, "patch-zipslip.zip"), new[]
        {
            new TestArchiveWriter.Entry { Name = "legit.txt", Content = Encoding.UTF8.GetBytes("this one is fine") },
            new TestArchiveWriter.Entry { Name = @"..\evil.txt", Content = Encoding.UTF8.GetBytes("zip slip payload A") },
            new TestArchiveWriter.Entry { Name = @"..\..\evil2.txt", Content = Encoding.UTF8.GetBytes("zip slip payload B") },
            new TestArchiveWriter.Entry { Name = @"sub\..\collapsed.txt", Content = Encoding.UTF8.GetBytes("benign parent collapse") },
            new TestArchiveWriter.Entry { Name = @"..\evil-dir\deep.txt", Content = Encoding.UTF8.GetBytes("zip slip payload C") },
            new TestArchiveWriter.Entry
            {
                Name = "link-to-windows.txt",
                Content = Encoding.UTF8.GetBytes(@"C:\Windows\system32\drivers\etc\hosts"),
                ExternalAttributes = 0xA1FF0000 // unix S_IFLNK
            }
        });

        // --- 4. Absolute path entries -----------------------------------------------------------------
        TestArchiveWriter.WriteZip(Path.Combine(ArchiveRoot, "patch-absolute.zip"), new[]
        {
            new TestArchiveWriter.Entry { Name = "ok.txt", Content = Encoding.UTF8.GetBytes("legit") },
            new TestArchiveWriter.Entry { Name = @"C:\Windows\Temp\galbox-evil.txt", Content = Encoding.UTF8.GetBytes("absolute payload A") },
            new TestArchiveWriter.Entry { Name = @"\\server\share\evil.txt", Content = Encoding.UTF8.GetBytes("unc payload") },
            new TestArchiveWriter.Entry { Name = @"\rooted.txt", Content = Encoding.UTF8.GetBytes("rooted payload") },
            new TestArchiveWriter.Entry { Name = @"D:\other-drive\evil.txt", Content = Encoding.UTF8.GetBytes("drive payload") }
        });

        // --- 5. Save data (full-CG style) -------------------------------------------------------------
        TestArchiveWriter.WriteZip(Path.Combine(ArchiveRoot, "patch-save.zip"), new[]
        {
            new TestArchiveWriter.Entry { Name = @"save\global.sav", Content = Deterministic(4096, 0x88) },
            new TestArchiveWriter.Entry { Name = @"save\save001.sav", Content = Deterministic(4096, 0x89) },
            new TestArchiveWriter.Entry { Name = @"save\save002.sav", Content = Deterministic(4096, 0x8A) },
            new TestArchiveWriter.Entry { Name = @"save\全CG存档说明.txt", Content = Encoding.UTF8.GetBytes("full CG save") }
        });

        // --- 6. Self-extracting executable (MZ header + a zip payload) ---------------------------------
        var sfxBody = Path.Combine(ArchiveRoot, "sfx-payload.zip");
        TestArchiveWriter.WriteZip(sfxBody, new[]
        {
            new TestArchiveWriter.Entry { Name = "should-never-be-extracted.txt", Content = Encoding.UTF8.GetBytes("nope") }
        });
        var sfxBytes = new List<byte> { (byte)'M', (byte)'Z' };
        sfxBytes.AddRange(new byte[2046]);
        sfxBytes.AddRange(File.ReadAllBytes(sfxBody));
        File.WriteAllBytes(Path.Combine(ArchiveRoot, "patch-sfx.exe"), sfxBytes.ToArray());
        File.Delete(sfxBody);

        // --- 7. Very long path (>260 characters) --------------------------------------------------------
        var deep = string.Join('\\', Enumerable.Range(0, 14).Select(i => $"nested_directory_{i:D2}"));
        var longRelative = deep + @"\deep-file.txt";
        TestArchiveWriter.WriteZip(Path.Combine(ArchiveRoot, "patch-longpath.zip"), new[]
        {
            new TestArchiveWriter.Entry { Name = longRelative, Content = Encoding.UTF8.GetBytes("long path payload") },
            new TestArchiveWriter.Entry { Name = @"plain\short.txt", Content = Encoding.UTF8.GetBytes("short payload") }
        });

        // --- 8. Reserved device names and invalid characters --------------------------------------------
        TestArchiveWriter.WriteZip(Path.Combine(ArchiveRoot, "patch-hostile-names.zip"), new[]
        {
            new TestArchiveWriter.Entry { Name = "CON.txt", Content = Encoding.UTF8.GetBytes("reserved") },
            new TestArchiveWriter.Entry { Name = @"aux\aux.dat", Content = Encoding.UTF8.GetBytes("reserved aux") },
            new TestArchiveWriter.Entry { Name = "bad<name>|who?.txt", Content = Encoding.UTF8.GetBytes("invalid chars") },
            new TestArchiveWriter.Entry { Name = "trailing-dot.", Content = Encoding.UTF8.GetBytes("trailing dot") },
            new TestArchiveWriter.Entry { Name = @"lpt1\report.txt", Content = Encoding.UTF8.GetBytes("reserved lpt1 as folder") },
            new TestArchiveWriter.Entry { Name = "fine.txt", Content = Encoding.UTF8.GetBytes("control file") }
        });

        // --- 9. Interruption scenario ------------------------------------------------------------------
        TestArchiveWriter.WriteZip(Path.Combine(ArchiveRoot, "patch-interrupt.zip"), new[]
        {
            new TestArchiveWriter.Entry { Name = "created-1.txt", Content = Encoding.UTF8.GetBytes("created 1") },
            new TestArchiveWriter.Entry { Name = "created-2.txt", Content = Encoding.UTF8.GetBytes("created 2") },
            new TestArchiveWriter.Entry { Name = "created-3.txt", Content = Encoding.UTF8.GetBytes("created 3") },
            new TestArchiveWriter.Entry { Name = @"sub\config.ini", Content = Encoding.UTF8.GetBytes("[config]\nlanguage=ja-interrupted\n") }
        });

        // --- 10. tar.gz: exercises the SharpCompress reader path -----------------------------------------
        TestArchiveWriter.WriteTarGz(Path.Combine(ArchiveRoot, "patch-targz.tar.gz"), new[]
        {
            ("targz/file-a.txt", Encoding.UTF8.GetBytes("tar entry A")),
            (@"..\evil-tar.txt", Encoding.UTF8.GetBytes("tar zip slip")),
            (@"targz/nested/..\collapsed-tar.txt", Encoding.UTF8.GetBytes("benign collapse inside tar")),
            (@"targz/nested/file-b.bin", Deterministic(3072, 0x99))
        });

        // --- 11. Retention scenario -----------------------------------------------------------------------
        // install #1 replaces an existing game file, so its backup is the only copy of the original -
        // which is exactly what makes "do not delete backups silently" a real requirement.
        TestArchiveWriter.WriteZip(Path.Combine(ArchiveRoot, "retention-1.zip"), new[]
        {
            new TestArchiveWriter.Entry { Name = "shared.txt", Content = Encoding.UTF8.GetBytes("shared file replaced by install #1\n") },
            new TestArchiveWriter.Entry { Name = "retention-1.txt", Content = Encoding.UTF8.GetBytes("install #1") }
        });

        for (var i = 2; i <= 6; i++)
        {
            TestArchiveWriter.WriteZip(Path.Combine(ArchiveRoot, $"retention-{i}.zip"), new[]
            {
                new TestArchiveWriter.Entry { Name = $"retention-{i}.txt", Content = Encoding.UTF8.GetBytes($"install #{i}") }
            });
        }
    }

    /// <summary>Deterministic pseudo-random content so every hash in this run is reproducible.</summary>
    public static byte[] Deterministic(int length, byte seed)
    {
        var buffer = new byte[length];
        var state = (uint)(seed * 2654435761u + 12345u);
        for (var i = 0; i < length; i++)
        {
            state = state * 1664525u + 1013904223u;
            buffer[i] = (byte)(state >> 24);
        }
        return buffer;
    }

    public string Archive(string fileName) => Path.Combine(ArchiveRoot, fileName);

    public static string Hash(byte[] content) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
}
