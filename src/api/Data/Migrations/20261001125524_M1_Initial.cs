using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M1_Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
