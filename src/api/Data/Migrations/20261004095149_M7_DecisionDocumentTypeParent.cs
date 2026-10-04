using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M7_DecisionDocumentTypeParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "document_type_parent",
                table: "decisions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "document_type_parent",
                table: "decisions");
        }
    }
}
