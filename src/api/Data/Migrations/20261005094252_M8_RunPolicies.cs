using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M8_RunPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "policies_proposed",
                table: "analysis_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "policies_proposed",
                table: "analysis_runs");
        }
    }
}
