using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoApi.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboxEvents",
                columns: table => new
                {
                    Id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EntityType = table.Column<int>(type: "int", nullable: false),
                    EntityId = table.Column<long>(type: "bigint", nullable: false),
                    Operation = table.Column<int>(type: "int", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxEvents", x => x.Id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_TodoListItem_UpdatedAt",
                table: "TodoListItem",
                column: "UpdatedAt"
            );

            migrationBuilder.CreateIndex(
                name: "IX_TodoList_UpdatedAt",
                table: "TodoList",
                column: "UpdatedAt"
            );

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEvents_EntityType_EntityId",
                table: "OutboxEvents",
                columns: new[] { "EntityType", "EntityId" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEvents_IdempotencyKey",
                table: "OutboxEvents",
                column: "IdempotencyKey",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEvents_OccurredAt",
                table: "OutboxEvents",
                column: "OccurredAt",
                filter: "[ProcessedAt] IS NULL"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "OutboxEvents");

            migrationBuilder.DropIndex(name: "IX_TodoListItem_UpdatedAt", table: "TodoListItem");

            migrationBuilder.DropIndex(name: "IX_TodoList_UpdatedAt", table: "TodoList");
        }
    }
}
