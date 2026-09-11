using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Galbox.Data.Migrations
{
    /// <summary>
    /// Baseline migration describing the schema exactly as the old <c>Database.EnsureCreated()</c> call used to
    /// create it (Games, UserSettings, Characters, Documents, MediaFiles, Screenshots, Patches, SaveBackups,
    /// ErrorRecords and their indexes).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This migration must never be edited.</b> It exists so that databases which already contain user data can
    /// be adopted into the migrations system: <see cref="GalboxDatabaseInitializer"/> detects a database that has
    /// tables but no <c>__EFMigrationsHistory</c> table, records this migration as applied ("baseline stamping")
    /// without running a single statement, and then applies every later migration normally.
    /// </para>
    /// <para>
    /// Because this baseline is generated from the same model that <c>EnsureCreated()</c> used, the stamped
    /// database and a freshly migrated database have the same structure — see
    /// <c>tests/Galbox.Data.Migrations.Harness</c>, which diffs the two schemas as part of the verification.
    /// </para>
    /// </remarks>
    public partial class InitialBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Games",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NameCn = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    NameOriginal = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    InstallPath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    MainExecutable = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    AlternativeExecutables = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 5000, nullable: true),
                    CoverImagePath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CoverImageUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    BackgroundImagePath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    BackgroundImageUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Developer = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReleaseDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Rating = table.Column<double>(type: "REAL", nullable: true),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: true),
                    TotalPlayTimeSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    LaunchCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSessionTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AddedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsScraped = table.Column<bool>(type: "INTEGER", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    EngineType = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Games", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EnableBangumi = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    EnableVndb = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    EnableYmgal = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    EnableCngal = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    BangumiAccessToken = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    BangumiRefreshToken = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    BangumiTokenExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BangumiUserId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    BangumiUsername = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    BangumiAuthMethod = table.Column<int>(type: "INTEGER", nullable: false),
                    SourcePriorityJson = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    MatchThresholdPercent = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 90),
                    AutoScrapeOnAdd = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    OnLaunchBehavior = table.Column<int>(type: "INTEGER", nullable: false),
                    OnExitBehavior = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoBackupOnExit = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    EnableBossKey = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    BossKeyModifiers = table.Column<int>(type: "INTEGER", nullable: false),
                    BossKeyVirtualKey = table.Column<uint>(type: "INTEGER", nullable: false),
                    MinimizeToTrayOnBossKey = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    ShowBossKeyNotification = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    DefaultBackupPath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    UseCustomBackupPath = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    ScreenshotPath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    EnableAdvancedMonitoring = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    AutoScreenshotOnExit = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    MonitoringIntervalMs = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1000),
                    ScreenshotFormat = table.Column<int>(type: "INTEGER", nullable: false),
                    JpgQuality = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 90),
                    Theme = table.Column<int>(type: "INTEGER", nullable: false),
                    Language = table.Column<int>(type: "INTEGER", nullable: false),
                    LibraryViewMode = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoScanOnStartup = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    DefaultScrapingSource = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    GameDirectoriesJson = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: true),
                    LastModified = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Characters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameInfoId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NameCn = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ImagePath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Role = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Characters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Characters_Games_GameInfoId",
                        column: x => x.GameInfoId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameInfoId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    PreviewContent = table.Column<string>(type: "TEXT", maxLength: 5000, nullable: true),
                    FileType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Documents_Games_GameInfoId",
                        column: x => x.GameInfoId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ErrorRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameInfoId = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    SolutionType = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    SolutionInstructions = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    DownloadUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DetectedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsResolved = table.Column<bool>(type: "INTEGER", nullable: false),
                    ResolvedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ErrorRecords_Games_GameInfoId",
                        column: x => x.GameInfoId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameInfoId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    DurationSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaFiles_Games_GameInfoId",
                        column: x => x.GameInfoId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Patches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameInfoId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    PatchType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DownloadUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    LocalPath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 5000, nullable: true),
                    AddedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DownloadedTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    InstalledTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DownloadProgress = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Patches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Patches_Games_GameInfoId",
                        column: x => x.GameInfoId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SaveBackups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameInfoId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    BackupPath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    OriginalSavePath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaveBackups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SaveBackups_Games_GameInfoId",
                        column: x => x.GameInfoId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Screenshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameInfoId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    CapturedTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Screenshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Screenshots_Games_GameInfoId",
                        column: x => x.GameInfoId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Characters_GameInfoId",
                table: "Characters",
                column: "GameInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_GameInfoId",
                table: "Documents",
                column: "GameInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorRecords_Category",
                table: "ErrorRecords",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorRecords_GameInfoId",
                table: "ErrorRecords",
                column: "GameInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorRecords_IsResolved",
                table: "ErrorRecords",
                column: "IsResolved");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorRecords_Severity",
                table: "ErrorRecords",
                column: "Severity");

            migrationBuilder.CreateIndex(
                name: "IX_Games_AddedTime",
                table: "Games",
                column: "AddedTime");

            migrationBuilder.CreateIndex(
                name: "IX_Games_InstallPath",
                table: "Games",
                column: "InstallPath");

            migrationBuilder.CreateIndex(
                name: "IX_Games_IsFavorite",
                table: "Games",
                column: "IsFavorite");

            migrationBuilder.CreateIndex(
                name: "IX_Games_NameCn",
                table: "Games",
                column: "NameCn");

            migrationBuilder.CreateIndex(
                name: "IX_Games_NameOriginal",
                table: "Games",
                column: "NameOriginal");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_GameInfoId",
                table: "MediaFiles",
                column: "GameInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_Patches_GameInfoId",
                table: "Patches",
                column: "GameInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_Patches_PatchType",
                table: "Patches",
                column: "PatchType");

            migrationBuilder.CreateIndex(
                name: "IX_Patches_Status",
                table: "Patches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SaveBackups_GameInfoId",
                table: "SaveBackups",
                column: "GameInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_Screenshots_GameInfoId",
                table: "Screenshots",
                column: "GameInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSettings_Id",
                table: "UserSettings",
                column: "Id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Characters");

            migrationBuilder.DropTable(
                name: "Documents");

            migrationBuilder.DropTable(
                name: "ErrorRecords");

            migrationBuilder.DropTable(
                name: "MediaFiles");

            migrationBuilder.DropTable(
                name: "Patches");

            migrationBuilder.DropTable(
                name: "SaveBackups");

            migrationBuilder.DropTable(
                name: "Screenshots");

            migrationBuilder.DropTable(
                name: "UserSettings");

            migrationBuilder.DropTable(
                name: "Games");
        }
    }
}
