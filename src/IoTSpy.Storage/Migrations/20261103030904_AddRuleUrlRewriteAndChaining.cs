using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoTSpy.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddRuleUrlRewriteAndChaining : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentRuleId",
                table: "ManipulationRules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RewriteHost",
                table: "ManipulationRules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RewritePath",
                table: "ManipulationRules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RewritePort",
                table: "ManipulationRules",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ParentRuleId",
                table: "ManipulationRules");

            migrationBuilder.DropColumn(
                name: "RewriteHost",
                table: "ManipulationRules");

            migrationBuilder.DropColumn(
                name: "RewritePath",
                table: "ManipulationRules");

            migrationBuilder.DropColumn(
                name: "RewritePort",
                table: "ManipulationRules");
        }
    }
}
