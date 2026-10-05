using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M8_SuggestionPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "keep_in_inbox",
                table: "suggestions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "policy_id",
                table: "suggestions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "policy_rule_id",
                table: "suggestions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_suggestions_policy_id_status",
                table: "suggestions",
                columns: new[] { "policy_id", "status" });

            migrationBuilder.AddForeignKey(
                name: "fk_suggestions_sender_policies_policy_id",
                table: "suggestions",
                column: "policy_id",
                principalTable: "sender_policies",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_suggestions_sender_policies_policy_id",
                table: "suggestions");

            migrationBuilder.DropIndex(
                name: "ix_suggestions_policy_id_status",
                table: "suggestions");

            migrationBuilder.DropColumn(
                name: "keep_in_inbox",
                table: "suggestions");

            migrationBuilder.DropColumn(
                name: "policy_id",
                table: "suggestions");

            migrationBuilder.DropColumn(
                name: "policy_rule_id",
                table: "suggestions");
        }
    }
}
