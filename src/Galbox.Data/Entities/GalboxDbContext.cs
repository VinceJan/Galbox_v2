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
    }
}