using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M8_ReanalysisComment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "owner_comment",
                table: "analysis_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_analysis_runs_owner_comment_length",
                table: "analysis_runs",
                sql: "char_length(owner_comment) <= 500");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_analysis_runs_owner_comment_length",
                table: "analysis_runs");

            migrationBuilder.DropColumn(
                name: "owner_comment",
                table: "analysis_runs");
        }
    }
}
