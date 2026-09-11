using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Galbox.Data.Migrations
{
    /// <summary>
    /// Adds the persisted game status (§4.1) and the save-node / save-group model (§3.3, §4.2),
    /// and links a save backup to the node it protects.
    /// </summary>
    /// <remarks>
    /// Every operation in <c>Up</c> is additive: new nullable columns, new columns with defaults, two new tables
    /// and indexes. No existing table is rebuilt and no existing value is dropped, so a user database that was
    /// created by the old <c>EnsureCreated()</c> path keeps its rows unchanged.
    /// <para>
    /// The only data write is the <c>Games.Status</c> backfill at the end of <c>Up</c>, which reproduces exactly
    /// the four status strings the old UI computed on the fly (see <see cref="Entities.GameStatusDerivation"/>),
    /// so a migrated library shows the same status as before instead of showing every game as "未玩过".
    /// </para>
    /// </remarks>
    public partial class AddGameStatusAndSaveNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SaveNodeId",
                table: "SaveBackups",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsStatusUserSet",
                table: "Games",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "Games",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "StatusChangedTime",
                table: "Games",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SaveGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameInfoId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RouteName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    OrderIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaveGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SaveGroups_Games_GameInfoId",
                        column: x => x.GameInfoId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SaveNodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameInfoId = table.Column<int>(type: "INTEGER", nullable: false),
                    SaveGroupId = table.Column<int>(type: "INTEGER", nullable: true),
                    SlotName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SceneLabel = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RouteName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ChapterName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ChapterProgressPercent = table.Column<int>(type: "INTEGER", nullable: true),
                    CgUnlockedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CgTotalCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CgUnlockPercent = table.Column<int>(type: "INTEGER", nullable: true),
                    CgUnlockedIdsJson = table.Column<string>(type: "TEXT", nullable: true),
                    PlayTimeSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveFilePath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    SaveSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveFileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SaveCreatedTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SaveModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsSnapshot = table.Column<bool>(type: "INTEGER", nullable: false),
                    SnapshotDescription = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    ParseStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    ParseError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ExtendedMetadataJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaveNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SaveNodes_Games_GameInfoId",
                        column: x => x.GameInfoId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SaveNodes_SaveGroups_SaveGroupId",
                        column: x => x.SaveGroupId,
                        principalTable: "SaveGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SaveBackups_SaveNodeId",
                table: "SaveBackups",
                column: "SaveNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_Games_Status",
                table: "Games",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SaveGroups_GameInfoId",
                table: "SaveGroups",
                column: "GameInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_SaveGroups_GameInfoId_OrderIndex",
                table: "SaveGroups",
                columns: new[] { "GameInfoId", "OrderIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_SaveNodes_GameInfoId",
                table: "SaveNodes",
                column: "GameInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_SaveNodes_GameInfoId_SaveModifiedTime",
                table: "SaveNodes",
                columns: new[] { "GameInfoId", "SaveModifiedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_SaveNodes_IsSnapshot",
                table: "SaveNodes",
                column: "IsSnapshot");

            migrationBuilder.CreateIndex(
                name: "IX_SaveNodes_SaveGroupId",
                table: "SaveNodes",
                column: "SaveGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_SaveNodes_SceneLabel",
                table: "SaveNodes",
                column: "SceneLabel");

            migrationBuilder.AddForeignKey(
                name: "FK_SaveBackups_SaveNodes_SaveNodeId",
                table: "SaveBackups",
                column: "SaveNodeId",
                principalTable: "SaveNodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // ---------------------------------------------------------------------------------------------
            // Data backfill: give every pre-existing game the status it was already showing in the UI.
            //
            // Before this migration the status was a display-time derivation in LibraryViewModel:
            //     LaunchCount == 0                 -> "从未游玩"  => GameStatus.NotPlayed (0, the column default)
            //     TotalPlayTimeSeconds > 3600      -> "已完成"    => 2
            //     LastSessionTime within 7 days    -> "正在游玩"  => 1
            //     otherwise                        -> "已游玩"    => 4 (OnHold)
            // The order of the statements below mirrors the order of those rules.
            //
            // "待玩" (PlanToPlay = 3) is intentionally never produced here: it is a user intent that cannot be
            // inferred from play statistics.
            //
            // datetime('now') returns UTC in "YYYY-MM-DD HH:MM:SS", which is lexicographically comparable with
            // the TEXT timestamp format EF Core writes ("YYYY-MM-DD HH:MM:SS.fffffff"), and LastSessionTime is
            // stored in UTC by the application.
            // ---------------------------------------------------------------------------------------------

            // LaunchCount == 0 keeps the column default (NotPlayed). Everything below only touches played games.
            // IsStatusUserSet = 0 guarantees that a status the user set by hand is never overwritten.
            migrationBuilder.Sql(
                "UPDATE \"Games\" SET \"Status\" = 2 " +
                "WHERE \"LaunchCount\" <> 0 AND \"TotalPlayTimeSeconds\" > 3600 AND \"IsStatusUserSet\" = 0;");

            migrationBuilder.Sql(
                "UPDATE \"Games\" SET \"Status\" = 1 " +
                "WHERE \"LaunchCount\" <> 0 AND \"TotalPlayTimeSeconds\" <= 3600 AND \"IsStatusUserSet\" = 0 " +
                "  AND \"LastSessionTime\" IS NOT NULL AND \"LastSessionTime\" >= datetime('now', '-7 days');");

            migrationBuilder.Sql(
                "UPDATE \"Games\" SET \"Status\" = 4 " +
                "WHERE \"LaunchCount\" <> 0 AND \"TotalPlayTimeSeconds\" <= 3600 AND \"IsStatusUserSet\" = 0 " +
                "  AND (\"LastSessionTime\" IS NULL OR \"LastSessionTime\" < datetime('now', '-7 days'));");

            // StatusChangedTime stays NULL on purpose: the backfill is not a user action.
        }

        /// <inheritdoc />
        /// <remarks>
        /// Dropping the <c>Status</c> column also removes the backfilled values; the pre-migration status text was
        /// derived from play statistics, which are untouched, so no user data is lost by rolling back.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SaveBackups_SaveNodes_SaveNodeId",
                table: "SaveBackups");

            migrationBuilder.DropTable(
                name: "SaveNodes");

            migrationBuilder.DropTable(
                name: "SaveGroups");

            migrationBuilder.DropIndex(
                name: "IX_SaveBackups_SaveNodeId",
                table: "SaveBackups");

            migrationBuilder.DropIndex(
                name: "IX_Games_Status",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "SaveNodeId",
                table: "SaveBackups");

            migrationBuilder.DropColumn(
                name: "IsStatusUserSet",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "StatusChangedTime",
                table: "Games");
        }
    }
}
