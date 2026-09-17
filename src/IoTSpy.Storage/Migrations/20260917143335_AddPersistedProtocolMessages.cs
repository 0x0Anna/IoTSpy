using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoTSpy.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddPersistedProtocolMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProtocolMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Protocol = table.Column<int>(type: "INTEGER", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PayloadPreview = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtocolMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProtocolMessages_DeviceId",
                table: "ProtocolMessages",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_ProtocolMessages_Protocol",
                table: "ProtocolMessages",
                column: "Protocol");

            migrationBuilder.CreateIndex(
                name: "IX_ProtocolMessages_Timestamp",
                table: "ProtocolMessages",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProtocolMessages");
        }
    }
}
