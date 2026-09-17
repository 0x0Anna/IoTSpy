using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoTSpy.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "Devices",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Tags",
                table: "Devices");
        }
    }
}
