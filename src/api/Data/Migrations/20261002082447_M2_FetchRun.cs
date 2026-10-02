using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M2_FetchRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_messages_from_address",
                table: "messages");

            migrationBuilder.CreateTable(
                name: "fetch_run_messages",
                columns: table => new
                {
                    message_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fetch_run_messages", x => x.message_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_messages_from_address_internal_date",
                table: "messages",
                columns: new[] { "from_address", "internal_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fetch_run_messages");

            migrationBuilder.DropIndex(
                name: "ix_messages_from_address_internal_date",
                table: "messages");

            migrationBuilder.CreateIndex(
                name: "ix_messages_from_address",
                table: "messages",
                column: "from_address");
        }
    }
}
