using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M6_FilterReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "filter_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    filter_count = table.Column<int>(type: "integer", nullable: false),
                    finding_count = table.Column<int>(type: "integer", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: true),
                    summary_model = table.Column<string>(type: "text", nullable: true),
                    summary_prompt_version = table.Column<string>(type: "text", nullable: true),
                    summary_error = table.Column<string>(type: "text", nullable: true),
                    summarised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_filter_reviews", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "filter_findings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    filter_ids = table.Column<List<string>>(type: "text[]", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    fix = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_filter_id = table.Column<string>(type: "text", nullable: true),
                    deleted_filter_ids = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_filter_findings", x => x.id);
                    table.ForeignKey(
                        name: "fk_filter_findings_filter_reviews_review_id",
                        column: x => x.review_id,
                        principalTable: "filter_reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_filter_findings_review_id_status",
                table: "filter_findings",
                columns: new[] { "review_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_filter_findings_status",
                table: "filter_findings",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_filter_reviews_created_at",
                table: "filter_reviews",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "filter_findings");

            migrationBuilder.DropTable(
                name: "filter_reviews");
        }
    }
}
