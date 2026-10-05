using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M8_SuggestionMailType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mail_type",
                table: "suggestions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "proposed_new_label",
                table: "suggestions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mail_type",
                table: "suggestion_alternatives",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "proposed_new_label",
                table: "suggestion_alternatives",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mail_type",
                table: "decisions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "mail_type",
                table: "suggestions");

            migrationBuilder.DropColumn(
                name: "proposed_new_label",
                table: "suggestions");

            migrationBuilder.DropColumn(
                name: "mail_type",
                table: "suggestion_alternatives");

            migrationBuilder.DropColumn(
                name: "proposed_new_label",
                table: "suggestion_alternatives");

            migrationBuilder.DropColumn(
                name: "mail_type",
                table: "decisions");
        }
    }
}
