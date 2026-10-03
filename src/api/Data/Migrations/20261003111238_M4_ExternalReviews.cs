using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M4_ExternalReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: false),
                    suggestion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sender_address = table.Column<string>(type: "text", nullable: false),
                    group_key = table.Column<string>(type: "text", nullable: true),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewer = table.Column<string>(type: "text", nullable: true),
                    reviewer_model = table.Column<string>(type: "text", nullable: true),
                    verdict = table.Column<string>(type: "text", nullable: true),
                    verdict_topic_label = table.Column<string>(type: "text", nullable: true),
                    verdict_needs_action = table.Column<bool>(type: "boolean", nullable: true),
                    verdict_to_be_deleted = table.Column<bool>(type: "boolean", nullable: true),
                    verdict_filter_criteria = table.Column<string>(type: "jsonb", nullable: true),
                    reasoning = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    resolution = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_reviews", x => x.id);
                    table.ForeignKey(
                        name: "fk_external_reviews_suggestions_suggestion_id",
                        column: x => x.suggestion_id,
                        principalTable: "suggestions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_external_reviews_batch_id",
                table: "external_reviews",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_reviews_sender_address_group_key",
                table: "external_reviews",
                columns: new[] { "sender_address", "group_key" });

            migrationBuilder.CreateIndex(
                name: "ix_external_reviews_sender_address_group_key1",
                table: "external_reviews",
                columns: new[] { "sender_address", "group_key" },
                unique: true,
                filter: "target_type = 'group' AND (status IN ('queued', 'running') OR (status = 'reviewed' AND resolution = 'none'))");

            migrationBuilder.CreateIndex(
                name: "ix_external_reviews_status_created_at",
                table: "external_reviews",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_external_reviews_suggestion_id",
                table: "external_reviews",
                column: "suggestion_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_reviews_suggestion_id1",
                table: "external_reviews",
                column: "suggestion_id",
                unique: true,
                filter: "target_type = 'suggestion' AND (status IN ('queued', 'running') OR (status = 'reviewed' AND resolution = 'none'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_reviews");
        }
    }
}
