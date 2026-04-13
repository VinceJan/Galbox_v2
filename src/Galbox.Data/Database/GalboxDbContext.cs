using Microsoft.EntityFrameworkCore;
using Galbox.Data.Entities;

namespace Galbox.Data.Database;

/// <summary>
/// SQLite database context for Galbox.
/// </summary>
public class GalboxDbContext : DbContext
{
    public DbSet<GameInfo> Games { get; set; }
    public DbSet<SaveRecord> Saves { get; set; }
    public DbSet<SaveGroup> SaveGroups { get; set; }
    public DbSet<PatchRecord> Patches { get; set; }

    private readonly string _dbPath;

    public GalboxDbContext()
    {
        // Default database path
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var galboxPath = Path.Combine(appDataPath, "Galbox");
        Directory.CreateDirectory(galboxPath);
        _dbPath = Path.Combine(galboxPath, "galbox.db");
    }

    public GalboxDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // GameInfo
        modelBuilder.Entity<GameInfo>(entity =>
        {
            entity.HasKey(e => e.GameId);
            entity.Property(e => e.NameCn).IsRequired().HasMaxLength(200);
            entity.Property(e => e.NameOriginal).HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.HasIndex(e => e.NameCn);
            entity.HasIndex(e => e.Status);
        });

        // SaveRecord
        modelBuilder.Entity<SaveRecord>(entity =>
        {
            entity.HasKey(e => e.SaveId);
            entity.HasOne(e => e.Game)
                  .WithMany(g => g.Saves)
                  .HasForeignKey(e => e.GameId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Group)
                  .WithMany(g => g.Saves)
                  .HasForeignKey(e => e.GroupId)
                  .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.GameId);
            entity.HasIndex(e => e.CreatedDate);
        });

        // SaveGroup
        modelBuilder.Entity<SaveGroup>(entity =>
        {
            entity.HasKey(e => e.GroupId);
            entity.HasOne(e => e.Game)
                  .WithMany(g => g.SaveGroups)
                  .HasForeignKey(e => e.GameId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.GameId);
        });

        // PatchRecord
        modelBuilder.Entity<PatchRecord>(entity =>
        {
            entity.HasKey(e => e.PatchId);
            entity.HasOne(e => e.Game)
                  .WithMany(g => g.Patches)
                  .HasForeignKey(e => e.GameId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.GameId);
        });
    }

    /// <summary>
    /// Initialize database (create tables if not exist).
    /// </summary>
    public void InitializeDatabase()
    {
        Database.EnsureCreated();
    }
}