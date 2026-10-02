using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M3_DecisionScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "scope_key",
                table: "decisions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_decisions_list_id_created_at",
                table: "decisions",
                columns: new[] { "list_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_decisions_scope_key_created_at",
                table: "decisions",
                columns: new[] { "scope_key", "created_at" });

            // Backfill as GroupKey.For computes it: List-Id trimmed and lower case; a sender scope needs the message's
            // category, so decisions whose message is gone stay without a scope (they never count towards a pattern).
            migrationBuilder.Sql(
                "UPDATE decisions SET list_id = NULLIF(lower(btrim(list_id, E' \\t\\r\\n')), '') WHERE list_id IS NOT NULL;");
            migrationBuilder.Sql(
                "UPDATE decisions SET scope_key = 'list:' || list_id || '|' || subject_template " +
                "WHERE list_id IS NOT NULL AND subject_template IS NOT NULL;");
            migrationBuilder.Sql(
                "UPDATE decisions d SET scope_key = 'from:' || m.from_address || '|' || coalesce(m.category, '-') || '|' || d.subject_template " +
                "FROM messages m WHERE m.id = d.message_id AND d.list_id IS NULL AND d.subject_template IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_decisions_list_id_created_at",
                table: "decisions");

            migrationBuilder.DropIndex(
                name: "ix_decisions_scope_key_created_at",
                table: "decisions");

            migrationBuilder.DropColumn(
                name: "scope_key",
                table: "decisions");
        }
    }
}
