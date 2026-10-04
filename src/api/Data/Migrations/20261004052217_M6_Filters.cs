using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M6_Filters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "filters_synced_at",
                table: "fetch_state",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "filters",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    criteria = table.Column<string>(type: "jsonb", nullable: false),
                    action = table.Column<string>(type: "jsonb", nullable: false),
                    criteria_summary = table.Column<string>(type: "text", nullable: false),
                    created_by_app = table.Column<bool>(type: "boolean", nullable: false),
                    restored_from = table.Column<string>(type: "text", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_app = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_filters", x => x.id);
                });

            migrationBuilder.UpdateData(
                table: "fetch_state",
                keyColumn: "id",
                keyValue: 1,
                column: "filters_synced_at",
                value: null);

            migrationBuilder.CreateIndex(
                name: "ix_filters_deleted_at",
                table: "filters",
                column: "deleted_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "filters");

            migrationBuilder.DropColumn(
                name: "filters_synced_at",
                table: "fetch_state");
        }
    }
}
