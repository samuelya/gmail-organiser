using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M8_AlternativeCopiedFrom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "copied",
                table: "suggestion_alternatives",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "copied_from_suggestion_id",
                table: "suggestion_alternatives",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "edited",
                table: "suggestion_alternatives",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "outcome_run_id",
                table: "suggestion_alternatives",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_suggestion_alternatives_copied_from_suggestion_id",
                table: "suggestion_alternatives",
                column: "copied_from_suggestion_id");

            migrationBuilder.CreateIndex(
                name: "ix_suggestion_alternatives_outcome_run_id",
                table: "suggestion_alternatives",
                column: "outcome_run_id");

            migrationBuilder.AddForeignKey(
                name: "fk_suggestion_alternatives_analysis_runs_outcome_run_id",
                table: "suggestion_alternatives",
                column: "outcome_run_id",
                principalTable: "analysis_runs",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_suggestion_alternatives_suggestions_copied_from_suggestion_",
                table: "suggestion_alternatives",
                column: "copied_from_suggestion_id",
                principalTable: "suggestions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_suggestion_alternatives_analysis_runs_outcome_run_id",
                table: "suggestion_alternatives");

            migrationBuilder.DropForeignKey(
                name: "fk_suggestion_alternatives_suggestions_copied_from_suggestion_",
                table: "suggestion_alternatives");

            migrationBuilder.DropIndex(
                name: "ix_suggestion_alternatives_copied_from_suggestion_id",
                table: "suggestion_alternatives");

            migrationBuilder.DropIndex(
                name: "ix_suggestion_alternatives_outcome_run_id",
                table: "suggestion_alternatives");

            migrationBuilder.DropColumn(
                name: "copied",
                table: "suggestion_alternatives");

            migrationBuilder.DropColumn(
                name: "copied_from_suggestion_id",
                table: "suggestion_alternatives");

            migrationBuilder.DropColumn(
                name: "edited",
                table: "suggestion_alternatives");

            migrationBuilder.DropColumn(
                name: "outcome_run_id",
                table: "suggestion_alternatives");
        }
    }
}
