using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M6_ExternalReviewTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "alternative_structure",
                table: "external_reviews",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "filter_finding_id",
                table: "external_reviews",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "label_plan_id",
                table: "external_reviews",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_external_reviews_open_filter_finding",
                table: "external_reviews",
                column: "filter_finding_id",
                unique: true,
                filter: "target_type = 'filter_finding' AND (status IN ('queued', 'running') OR (status = 'reviewed' AND resolution = 'none'))");

            migrationBuilder.CreateIndex(
                name: "ux_external_reviews_open_label_plan",
                table: "external_reviews",
                column: "label_plan_id",
                unique: true,
                filter: "target_type = 'label_plan' AND (status IN ('queued', 'running') OR (status = 'reviewed' AND resolution = 'none'))");

            migrationBuilder.AddForeignKey(
                name: "fk_external_reviews_filter_findings_filter_finding_id",
                table: "external_reviews",
                column: "filter_finding_id",
                principalTable: "filter_findings",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_external_reviews_label_plans_label_plan_id",
                table: "external_reviews",
                column: "label_plan_id",
                principalTable: "label_plans",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_external_reviews_filter_findings_filter_finding_id",
                table: "external_reviews");

            migrationBuilder.DropForeignKey(
                name: "fk_external_reviews_label_plans_label_plan_id",
                table: "external_reviews");

            migrationBuilder.DropIndex(
                name: "ux_external_reviews_open_filter_finding",
                table: "external_reviews");

            migrationBuilder.DropIndex(
                name: "ux_external_reviews_open_label_plan",
                table: "external_reviews");

            migrationBuilder.DropColumn(
                name: "alternative_structure",
                table: "external_reviews");

            migrationBuilder.DropColumn(
                name: "filter_finding_id",
                table: "external_reviews");

            migrationBuilder.DropColumn(
                name: "label_plan_id",
                table: "external_reviews");
        }
    }
}
