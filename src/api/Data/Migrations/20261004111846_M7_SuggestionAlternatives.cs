using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M7_SuggestionAlternatives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "analysis_runs",
                type: "text",
                nullable: false,
                defaultValueSql: "'analyse'");

            migrationBuilder.CreateTable(
                name: "suggestion_alternatives",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    suggestion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<string>(type: "text", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    group_key = table.Column<string>(type: "text", nullable: true),
                    topic_label = table.Column<string>(type: "text", nullable: false),
                    is_new_label = table.Column<bool>(type: "boolean", nullable: false),
                    document_type_label = table.Column<string>(type: "text", nullable: true),
                    document_type_is_new = table.Column<bool>(type: "boolean", nullable: false),
                    replace_label_ids = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    replace_labels = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    needs_action = table.Column<bool>(type: "boolean", nullable: false),
                    to_be_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    unsubscribe_suggested = table.Column<bool>(type: "boolean", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    filter_criteria = table.Column<string>(type: "jsonb", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suggestion_alternatives", x => x.id);
                    table.CheckConstraint("ck_suggestion_alternatives_confidence", "confidence BETWEEN 0 AND 1");
                    table.ForeignKey(
                        name: "fk_suggestion_alternatives_analysis_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "analysis_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_suggestion_alternatives_suggestions_suggestion_id",
                        column: x => x.suggestion_id,
                        principalTable: "suggestions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_suggestion_alternatives_run_id",
                table: "suggestion_alternatives",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_suggestion_alternatives_suggestion_id",
                table: "suggestion_alternatives",
                column: "suggestion_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "suggestion_alternatives");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "analysis_runs");
        }
    }
}
