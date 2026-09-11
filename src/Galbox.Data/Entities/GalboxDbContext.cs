using Microsoft.EntityFrameworkCore;

namespace Galbox.Data.Entities;

/// <summary>
/// Database context for Galbox application.
/// </summary>
public class GalboxDbContext : DbContext
{
    /// <summary>
    /// Creates a GalboxDbContext with the specified options.
    /// </summary>
    public GalboxDbContext(DbContextOptions<GalboxDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// Games in the library.
    /// </summary>
    public DbSet<GameInfo> Games => Set<GameInfo>();

    /// <summary>
    /// User settings/preferences.
    /// </summary>
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();

    /// <summary>
    /// Characters associated with games.
    /// </summary>
    public DbSet<GameCharacter> Characters => Set<GameCharacter>();

    /// <summary>
    /// Documents in game folders.
    /// </summary>
    public DbSet<GameDocument> Documents => Set<GameDocument>();

    /// <summary>
    /// Media files associated with games.
    /// </summary>
    public DbSet<GameMediaFile> MediaFiles => Set<GameMediaFile>();

    /// <summary>
    /// Screenshots for games.
    /// </summary>
    public DbSet<GameScreenshot> Screenshots => Set<GameScreenshot>();

    /// <summary>
    /// Save backups for games.
    /// </summary>
    public DbSet<GameSaveBackup> SaveBackups => Set<GameSaveBackup>();

    /// <summary>
    /// Save nodes (story positions and snapshots) for games.
    /// </summary>
    public DbSet<SaveNode> SaveNodes => Set<SaveNode>();

    /// <summary>
    /// Save groups (branch/route groupings) for games.
    /// </summary>
    public DbSet<SaveGroup> SaveGroups => Set<SaveGroup>();

    /// <summary>
    /// Patch records for games.
    /// </summary>
    public DbSet<PatchRecord> Patches => Set<PatchRecord>();

    /// <summary>
    /// Error records for games.
    /// </summary>
    public DbSet<GameErrorRecord> ErrorRecords => Set<GameErrorRecord>();

    /// <summary>
    /// Configures the entity models.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // GameInfo configuration
        modelBuilder.Entity<GameInfo>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.NameCn);
            entity.HasIndex(e => e.NameOriginal);
            entity.HasIndex(e => e.InstallPath);
            entity.HasIndex(e => e.IsFavorite);
            entity.HasIndex(e => e.AddedTime);

            entity.Property(e => e.NameOriginal).IsRequired().HasMaxLength(500);
            entity.Property(e => e.InstallPath).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.MainExecutable).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.NameCn).HasMaxLength(500);
            entity.Property(e => e.Description).HasMaxLength(5000);
            entity.Property(e => e.CoverImagePath).HasMaxLength(2000);
            entity.Property(e => e.CoverImageUrl).HasMaxLength(2000);
            entity.Property(e => e.BackgroundImagePath).HasMaxLength(2000);
            entity.Property(e => e.BackgroundImageUrl).HasMaxLength(2000);
            entity.Property(e => e.Developer).HasMaxLength(200);
            entity.Property(e => e.SourceId).HasMaxLength(100);
            entity.Property(e => e.SourceType).HasMaxLength(50);
            entity.Property(e => e.VndbId).HasMaxLength(20);

            // EngineType 枚举配置
            entity.Property(e => e.EngineType)
                .HasDefaultValue(GameEngineType.Unknown)
                .HasConversion<int>();

            // GameStatus 枚举配置：持久化的五档游戏状态（§4.1），新库/迁移时的默认值为"未玩过"
            entity.Property(e => e.Status)
                .HasDefaultValue(GameStatus.NotPlayed)
                .HasConversion<int>();

            entity.Property(e => e.IsStatusUserSet)
                .HasDefaultValue(false);

            entity.HasIndex(e => e.Status);

            // Relationships
            entity.HasMany(e => e.Characters)
                .WithOne(e => e.GameInfo)
                .HasForeignKey(e => e.GameInfoId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Documents)
                .WithOne(e => e.GameInfo)
                .HasForeignKey(e => e.GameInfoId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.MediaFiles)
                .WithOne(e => e.GameInfo)
                .HasForeignKey(e => e.GameInfoId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Screenshots)
                .WithOne(e => e.GameInfo)
                .HasForeignKey(e => e.GameInfoId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.SaveBackups)
                .WithOne(e => e.GameInfo)
                .HasForeignKey(e => e.GameInfoId)
                .OnDelete(DeleteBehavior.Cascade);

            // SaveNode / SaveGroup relationships are configured in their own blocks below.
        });

        // GameCharacter configuration
        modelBuilder.Entity<GameCharacter>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.NameCn).HasMaxLength(200);
            entity.Property(e => e.ImagePath).HasMaxLength(2000);
            entity.Property(e => e.ImageUrl).HasMaxLength(2000);
            entity.Property(e => e.Role).HasMaxLength(100);
        });

        // GameDocument configuration
        modelBuilder.Entity<GameDocument>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.PreviewContent).HasMaxLength(5000);
            entity.Property(e => e.FileType).HasMaxLength(50);
        });

        // GameMediaFile configuration
        modelBuilder.Entity<GameMediaFile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.MediaType).HasMaxLength(50);
        });

        // GameScreenshot configuration
        modelBuilder.Entity<GameScreenshot>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.Url).HasMaxLength(2000);
        });

        // GameSaveBackup configuration
        modelBuilder.Entity<GameSaveBackup>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(500);
            entity.Property(e => e.BackupPath).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.OriginalSavePath).HasMaxLength(2000);
            entity.Property(e => e.Description).HasMaxLength(500);

            entity.HasIndex(e => e.SaveNodeId);

            // A backup optionally protects one save node; deleting the node must not delete the backup.
            entity.HasOne(e => e.SaveNode)
                .WithMany(e => e.Backups)
                .HasForeignKey(e => e.SaveNodeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // SaveGroup configuration (branch/route grouping - §3.3 "存档组")
        modelBuilder.Entity<SaveGroup>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.RouteName).HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);

            entity.HasIndex(e => e.GameInfoId);
            entity.HasIndex(e => new { e.GameInfoId, e.OrderIndex });

            entity.HasOne(e => e.GameInfo)
                .WithMany(e => e.SaveGroups)
                .HasForeignKey(e => e.GameInfoId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // SaveNode configuration (story position / snapshot - §3.3 and §4.2)
        modelBuilder.Entity<SaveNode>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SlotName).HasMaxLength(200);
            entity.Property(e => e.SceneLabel).HasMaxLength(500);
            entity.Property(e => e.RouteName).HasMaxLength(200);
            entity.Property(e => e.ChapterName).HasMaxLength(200);
            entity.Property(e => e.SaveFilePath).HasMaxLength(2000);
            entity.Property(e => e.SnapshotDescription).HasMaxLength(1000);
            entity.Property(e => e.ParseError).HasMaxLength(1000);

            entity.Property(e => e.Source).HasConversion<int>();
            entity.Property(e => e.ParseStatus).HasConversion<int>();

            entity.HasIndex(e => e.GameInfoId);
            entity.HasIndex(e => e.SaveGroupId);
            entity.HasIndex(e => e.SceneLabel);
            entity.HasIndex(e => e.IsSnapshot);

            // Timeline queries: "all nodes of this game, ordered by when the save was written".
            entity.HasIndex(e => new { e.GameInfoId, e.SaveModifiedTime });

            entity.HasOne(e => e.GameInfo)
                .WithMany(e => e.SaveNodes)
                .HasForeignKey(e => e.GameInfoId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting a group keeps its nodes and just unfiles them.
            entity.HasOne(e => e.SaveGroup)
                .WithMany(e => e.Nodes)
                .HasForeignKey(e => e.SaveGroupId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // PatchRecord configuration
        modelBuilder.Entity<PatchRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.GameInfoId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.PatchType);

            entity.Property(e => e.Name).IsRequired().HasMaxLength(500);
            entity.Property(e => e.PatchType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Version).HasMaxLength(100);
            entity.Property(e => e.Source).HasMaxLength(200);
            entity.Property(e => e.DownloadUrl).HasMaxLength(2000);
            entity.Property(e => e.LocalPath).HasMaxLength(2000);
            entity.Property(e => e.Description).HasMaxLength(5000);
            entity.Property(e => e.ExternalId).HasMaxLength(100);

            // Relationships
            entity.HasOne(e => e.GameInfo)
                .WithMany()
                .HasForeignKey(e => e.GameInfoId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // GameErrorRecord configuration
        modelBuilder.Entity<GameErrorRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.GameInfoId);
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => e.Severity);
            entity.HasIndex(e => e.IsResolved);

            entity.Property(e => e.Category).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Severity).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.SolutionType).IsRequired().HasMaxLength(30);
            entity.Property(e => e.SolutionInstructions).HasMaxLength(2000);
            entity.Property(e => e.DownloadUrl).HasMaxLength(500);
            entity.Property(e => e.ToolName).HasMaxLength(100);

            // Relationships
            entity.HasOne(e => e.GameInfo)
                .WithMany()
                .HasForeignKey(e => e.GameInfoId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // UserSettings configuration
        modelBuilder.Entity<UserSettings>(entity =>
        {
            entity.ToTable("UserSettings");
            entity.HasKey(e => e.Id);

            // 字符串长度约束
            entity.Property(e => e.DefaultScrapingSource).HasMaxLength(50);
            entity.Property(e => e.GameDirectoriesJson).HasMaxLength(10000);
            entity.Property(e => e.BangumiAccessToken).HasMaxLength(500);
            entity.Property(e => e.BangumiRefreshToken).HasMaxLength(500);
            entity.Property(e => e.BangumiUserId).HasMaxLength(100);
            entity.Property(e => e.BangumiUsername).HasMaxLength(100);
            entity.Property(e => e.SourcePriorityJson).HasMaxLength(500);
            entity.Property(e => e.DefaultBackupPath).HasMaxLength(2000);
            entity.Property(e => e.ScreenshotPath).HasMaxLength(2000);

            // 枚举类型转换
            entity.Property(e => e.BangumiAuthMethod).HasConversion<int>();
            entity.Property(e => e.OnLaunchBehavior).HasConversion<int>();
            entity.Property(e => e.OnExitBehavior).HasConversion<int>();
            entity.Property(e => e.BossKeyModifiers).HasConversion<int>();
            entity.Property(e => e.ScreenshotFormat).HasConversion<int>();
            entity.Property(e => e.Theme).HasConversion<int>();
            entity.Property(e => e.Language).HasConversion<int>();
            entity.Property(e => e.LibraryViewMode).HasConversion<int>();

            // 默认值
            entity.Property(e => e.EnableBossKey).HasDefaultValue(true);
            entity.Property(e => e.EnableBangumi).HasDefaultValue(true);
            entity.Property(e => e.EnableVndb).HasDefaultValue(true);
            entity.Property(e => e.EnableYmgal).HasDefaultValue(true);
            entity.Property(e => e.EnableCngal).HasDefaultValue(true);
            entity.Property(e => e.AutoScrapeOnAdd).HasDefaultValue(true);
            entity.Property(e => e.MatchThresholdPercent).HasDefaultValue(90);
            entity.Property(e => e.MonitoringIntervalMs).HasDefaultValue(1000);
            entity.Property(e => e.JpgQuality).HasDefaultValue(90);
            entity.Property(e => e.UseCustomBackupPath).HasDefaultValue(false);
            entity.Property(e => e.AutoBackupOnExit).HasDefaultValue(false);
            entity.Property(e => e.AutoScreenshotOnExit).HasDefaultValue(false);
            entity.Property(e => e.EnableAdvancedMonitoring).HasDefaultValue(false);
            entity.Property(e => e.AutoScanOnStartup).HasDefaultValue(false);
            entity.Property(e => e.MinimizeToTrayOnBossKey).HasDefaultValue(true);
            entity.Property(e => e.ShowBossKeyNotification).HasDefaultValue(true);

            // 索引
            entity.HasIndex(e => e.Id).IsUnique();
        });
    }
}