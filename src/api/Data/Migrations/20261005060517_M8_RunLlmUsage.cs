using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M8_RunLlmUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "completion_tokens",
                table: "analysis_runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "llm_milliseconds",
                table: "analysis_runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "near_context_limit",
                table: "analysis_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "prompt_tokens",
                table: "analysis_runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "completion_tokens",
                table: "analysis_runs");

            migrationBuilder.DropColumn(
                name: "llm_milliseconds",
                table: "analysis_runs");

            migrationBuilder.DropColumn(
                name: "near_context_limit",
                table: "analysis_runs");

            migrationBuilder.DropColumn(
                name: "prompt_tokens",
                table: "analysis_runs");
        }
    }
}
