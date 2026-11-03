using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoTSpy.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddTlsPassthroughSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoPassthroughOnTlsFailure",
                table: "ProxySettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TlsPassthroughHosts",
                table: "ProxySettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoPassthroughOnTlsFailure",
                table: "ProxySettings");

            migrationBuilder.DropColumn(
                name: "TlsPassthroughHosts",
                table: "ProxySettings");
        }
    }
}
