using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M7_DocumentTypeLabel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "document_type_is_new",
                table: "suggestions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "document_type_label",
                table: "suggestions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "document_type_decided",
                table: "decisions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "document_type_label",
                table: "decisions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "document_type_is_new",
                table: "suggestions");

            migrationBuilder.DropColumn(
                name: "document_type_label",
                table: "suggestions");

            migrationBuilder.DropColumn(
                name: "document_type_decided",
                table: "decisions");

            migrationBuilder.DropColumn(
                name: "document_type_label",
                table: "decisions");
        }
    }
}
