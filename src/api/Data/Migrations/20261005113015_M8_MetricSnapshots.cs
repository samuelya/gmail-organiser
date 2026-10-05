using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M8_MetricSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "metric_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    taken_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    messages_total = table.Column<int>(type: "integer", nullable: false),
                    inbox_count = table.Column<int>(type: "integer", nullable: false),
                    inbox_unread_count = table.Column<int>(type: "integer", nullable: false),
                    covered_by_policy = table.Column<int>(type: "integer", nullable: false),
                    covered_by_filter = table.Column<int>(type: "integer", nullable: false),
                    analysed = table.Column<int>(type: "integer", nullable: false),
                    applied = table.Column<int>(type: "integer", nullable: false),
                    to_be_deleted = table.Column<int>(type: "integer", nullable: false),
                    llm_milliseconds_total = table.Column<long>(type: "bigint", nullable: false),
                    prompt_tokens_total = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metric_snapshots", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_metric_snapshots_taken_at",
                table: "metric_snapshots",
                column: "taken_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "metric_snapshots");
        }
    }
}
