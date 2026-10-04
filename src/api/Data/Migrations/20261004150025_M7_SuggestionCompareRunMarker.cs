using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M7_SuggestionCompareRunMarker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "compare_run_created_at",
                table: "suggestions",
                type: "timestamp with time zone",
                nullable: true);

            // Suggestions that already hold an alternative take its run's created_at, as the compare run would have set it.
            migrationBuilder.Sql(
                """
                UPDATE suggestions s SET compare_run_created_at = r.created_at
                FROM suggestion_alternatives a JOIN analysis_runs r ON r.id = a.run_id
                WHERE a.suggestion_id = s.id
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "compare_run_created_at",
                table: "suggestions");
        }
    }
}
