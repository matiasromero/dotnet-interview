using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoApi.Migrations
{
    /// <inheritdoc />
    public partial class AddTodoListItemUpdatedAtAndItemSyncSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "TodoListItem",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()"
            );

            migrationBuilder.AddColumn<string>(
                name: "ParentExternalId",
                table: "SyncMappings",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "UpdatedAt", table: "TodoListItem");

            migrationBuilder.DropColumn(name: "ParentExternalId", table: "SyncMappings");
        }
    }
}
