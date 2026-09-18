using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoTSpy.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddHostBaselines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostBaselines",
                columns: table => new
                {
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    SampleCount = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationMean = table.Column<double>(type: "REAL", nullable: false),
                    DurationM2 = table.Column<double>(type: "REAL", nullable: false),
                    SizeMean = table.Column<double>(type: "REAL", nullable: false),
                    SizeM2 = table.Column<double>(type: "REAL", nullable: false),
                    StatusCodeCountsJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostBaselines", x => x.Host);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostBaselines_UpdatedAt",
                table: "HostBaselines",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostBaselines");
        }
    }
}
