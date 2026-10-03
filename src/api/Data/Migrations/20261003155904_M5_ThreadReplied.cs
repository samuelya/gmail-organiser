using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M5_ThreadReplied : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "thread_replied",
                table: "messages",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_thread_id",
                table: "messages",
                column: "thread_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_messages_thread_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "thread_replied",
                table: "messages");
        }
    }
}
