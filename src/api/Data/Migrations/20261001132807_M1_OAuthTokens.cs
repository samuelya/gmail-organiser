using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M1_OAuthTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oauth_tokens",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    account_email = table.Column<string>(type: "text", nullable: false),
                    refresh_token_protected = table.Column<string>(type: "text", nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    connected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_tokens", x => x.id);
                    table.CheckConstraint("ck_oauth_tokens_singleton", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oauth_tokens");
        }
    }
}
