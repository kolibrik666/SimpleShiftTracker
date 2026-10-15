using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tracker.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DefaultHourlyRateCents = table.Column<long>(type: "INTEGER", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                    table.CheckConstraint("CK_Settings_Rate", "DefaultHourlyRateCents > 0");
                });

            migrationBuilder.CreateTable(
                name: "Shifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    HourlyRateCents = table.Column<long>(type: "INTEGER", nullable: false),
                    LastCheckpointUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shifts", x => x.Id);
                    table.CheckConstraint("CK_Shifts_Rate", "HourlyRateCents > 0");
                    table.CheckConstraint("CK_Shifts_Times", "EndedUtc IS NULL OR EndedUtc >= StartedUtc");
                });

            migrationBuilder.CreateTable(
                name: "WorkIntervals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShiftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkIntervals", x => x.Id);
                    table.CheckConstraint("CK_Intervals_Times", "EndedUtc IS NULL OR EndedUtc >= StartedUtc");
                    table.ForeignKey(
                        name: "FK_WorkIntervals_Shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "Shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Shifts_StartedUtc",
                table: "Shifts",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkIntervals_OneOpen",
                table: "WorkIntervals",
                column: "ShiftId",
                unique: true,
                filter: "EndedUtc IS NULL");

            // SQLite expression index enforces one unfinished shift across the whole table.
            migrationBuilder.Sql("CREATE UNIQUE INDEX IX_Shifts_OneUnfinished ON Shifts ((1)) WHERE EndedUtc IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "WorkIntervals");

            migrationBuilder.DropTable(
                name: "Shifts");
        }
    }
}
