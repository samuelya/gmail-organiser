using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M2_JobDedupKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_jobs_type_active",
                table: "jobs");

            migrationBuilder.AddColumn<string>(
                name: "dedup_key",
                table: "jobs",
                type: "text",
                nullable: true);

            // Sender fetches queued before this migration keep their target as the key.
            migrationBuilder.Sql("UPDATE jobs SET dedup_key = cursor->>'target' WHERE type = 'sender_fetch' AND cursor IS NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "ux_jobs_type_dedup_key_active",
                table: "jobs",
                columns: new[] { "type", "dedup_key" },
                unique: true,
                filter: "status IN ('queued', 'running', 'paused')")
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_jobs_type_dedup_key_active",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "dedup_key",
                table: "jobs");

            migrationBuilder.CreateIndex(
                name: "ux_jobs_type_active",
                table: "jobs",
                column: "type",
                unique: true,
                filter: "status IN ('queued', 'running', 'paused')");
        }
    }
}
