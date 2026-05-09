using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoApi.Migrations
{
    /// <inheritdoc />
    public partial class ExtendSyncMappingForReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExternalUpdatedAtAtSync",
                table: "SyncMappings",
                type: "datetime2",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "IdempotencyKey",
                table: "SyncMappings",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWID()"
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "LocalUpdatedAtAtSync",
                table: "SyncMappings",
                type: "datetime2",
                nullable: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_SyncMappings_IdempotencyKey",
                table: "SyncMappings",
                column: "IdempotencyKey",
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SyncMappings_IdempotencyKey",
                table: "SyncMappings"
            );

            migrationBuilder.DropColumn(name: "ExternalUpdatedAtAtSync", table: "SyncMappings");

            migrationBuilder.DropColumn(name: "IdempotencyKey", table: "SyncMappings");

            migrationBuilder.DropColumn(name: "LocalUpdatedAtAtSync", table: "SyncMappings");
        }
    }
}
