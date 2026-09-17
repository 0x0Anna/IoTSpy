using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IoTSpy.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledScanTargetLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite has no ALTER COLUMN; relaxing DeviceId to nullable needs a full table
            // rebuild. The default EF-generated AlterColumn for this emits a PRAGMA
            // foreign_keys toggle that can't run inside EF's migration transaction (same
            // foot-gun documented for DecoupleContentRules) — do the rebuild by hand
            // with suppressTransaction instead.
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);

            migrationBuilder.Sql(@"
                CREATE TABLE ScheduledScans_new (
                    Id             TEXT NOT NULL CONSTRAINT PK_ScheduledScans PRIMARY KEY,
                    DeviceId       TEXT NULL,
                    TargetCidr     TEXT NULL,
                    TargetTag      TEXT NULL,
                    CronExpression TEXT NOT NULL,
                    IsEnabled      INTEGER NOT NULL,
                    LastRunAt      INTEGER NULL,
                    NextRunAt      INTEGER NULL,
                    LastScanJobId  TEXT NULL,
                    LastRunStatus  INTEGER NULL,
                    LastRunError   TEXT NULL,
                    CreatedAt      INTEGER NOT NULL,
                    CONSTRAINT FK_ScheduledScans_Devices_DeviceId
                        FOREIGN KEY (DeviceId)
                        REFERENCES Devices (Id)
                        ON DELETE CASCADE
                );");

            migrationBuilder.Sql(@"
                INSERT INTO ScheduledScans_new (
                    Id, DeviceId, CronExpression, IsEnabled, LastRunAt, NextRunAt,
                    LastScanJobId, LastRunStatus, LastRunError, CreatedAt
                )
                SELECT
                    Id, DeviceId, CronExpression, IsEnabled, LastRunAt, NextRunAt,
                    LastScanJobId, LastRunStatus, LastRunError, CreatedAt
                FROM ScheduledScans;");

            migrationBuilder.Sql("DROP TABLE ScheduledScans;");
            migrationBuilder.Sql("ALTER TABLE ScheduledScans_new RENAME TO ScheduledScans;");

            migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledScans_DeviceId",
                table: "ScheduledScans",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledScans_IsEnabled",
                table: "ScheduledScans",
                column: "IsEnabled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Any CIDR/tag-targeted schedule (DeviceId NULL) has no meaningful DeviceId to
            // roll back to; coalesce to the zero GUID, matching the placeholder convention
            // already used elsewhere in this migration history (e.g. the auto-generated
            // Down for nullable-Guid AlterColumn defaults).
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);

            migrationBuilder.Sql(@"
                CREATE TABLE ScheduledScans_old (
                    Id             TEXT NOT NULL CONSTRAINT PK_ScheduledScans PRIMARY KEY,
                    DeviceId       TEXT NOT NULL,
                    CronExpression TEXT NOT NULL,
                    IsEnabled      INTEGER NOT NULL,
                    LastRunAt      INTEGER NULL,
                    NextRunAt      INTEGER NULL,
                    LastScanJobId  TEXT NULL,
                    LastRunStatus  INTEGER NULL,
                    LastRunError   TEXT NULL,
                    CreatedAt      INTEGER NOT NULL,
                    CONSTRAINT FK_ScheduledScans_Devices_DeviceId
                        FOREIGN KEY (DeviceId)
                        REFERENCES Devices (Id)
                        ON DELETE CASCADE
                );");

            migrationBuilder.Sql(@"
                INSERT INTO ScheduledScans_old (
                    Id, DeviceId, CronExpression, IsEnabled, LastRunAt, NextRunAt,
                    LastScanJobId, LastRunStatus, LastRunError, CreatedAt
                )
                SELECT
                    Id, COALESCE(DeviceId, '00000000-0000-0000-0000-000000000000'),
                    CronExpression, IsEnabled, LastRunAt, NextRunAt,
                    LastScanJobId, LastRunStatus, LastRunError, CreatedAt
                FROM ScheduledScans;");

            migrationBuilder.Sql("DROP TABLE ScheduledScans;");
            migrationBuilder.Sql("ALTER TABLE ScheduledScans_old RENAME TO ScheduledScans;");

            migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledScans_DeviceId",
                table: "ScheduledScans",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledScans_IsEnabled",
                table: "ScheduledScans",
                column: "IsEnabled");
        }
    }
}
