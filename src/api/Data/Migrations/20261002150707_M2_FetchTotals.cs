using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M2_FetchTotals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "all_mail_total",
                table: "fetch_state",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "inbox_total",
                table: "fetch_state",
                type: "bigint",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "fetch_state",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "all_mail_total", "inbox_total" },
                values: new object[] { null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "all_mail_total",
                table: "fetch_state");

            migrationBuilder.DropColumn(
                name: "inbox_total",
                table: "fetch_state");
        }
    }
}
