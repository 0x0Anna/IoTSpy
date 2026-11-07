using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoTSpy.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddProxySettingsIsPassive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPassive",
                table: "ProxySettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Passive used to be ProxyMode value 3 (always the explicit listener). Rewrite it to
            // ExplicitProxy (0) + IsPassive; ProxySettingsRepository also normalizes on read.
            migrationBuilder.Sql("UPDATE \"ProxySettings\" SET \"Mode\" = 0, \"IsPassive\" = 1 WHERE \"Mode\" = 3;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"ProxySettings\" SET \"Mode\" = 3 WHERE \"IsPassive\" = 1 AND \"Mode\" = 0;");
            migrationBuilder.DropColumn(
                name: "IsPassive",
                table: "ProxySettings");
        }
    }
}
