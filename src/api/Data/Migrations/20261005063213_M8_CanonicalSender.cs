using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M8_CanonicalSender : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "canonical_address",
                table: "senders",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "canonical_domain",
                table: "senders",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "is_relay",
                table: "senders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "canonical_address",
                table: "messages",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "canonical_domain",
                table: "messages",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Existing rows start as their own canonical sender; the canonical_backfill job decodes the relay ones.
            // The domain is the text after the LAST '@', as SenderAddress.Domain has it ('' when there is none).
            migrationBuilder.Sql("UPDATE messages SET canonical_address = from_address, canonical_domain = coalesce(substring(from_address from '@([^@]*)$'), '');");
            migrationBuilder.Sql("UPDATE senders SET canonical_address = address, canonical_domain = coalesce(substring(address from '@([^@]*)$'), '');");

            migrationBuilder.CreateIndex(
                name: "ix_senders_canonical_address",
                table: "senders",
                column: "canonical_address");

            migrationBuilder.CreateIndex(
                name: "ix_senders_canonical_address_trgm",
                table: "senders",
                column: "canonical_address")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_senders_canonical_domain",
                table: "senders",
                column: "canonical_domain");

            migrationBuilder.CreateIndex(
                name: "ix_senders_canonical_domain_trgm",
                table: "senders",
                column: "canonical_domain")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_canonical_address",
                table: "messages",
                column: "canonical_address");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_senders_canonical_address",
                table: "senders");

            migrationBuilder.DropIndex(
                name: "ix_senders_canonical_address_trgm",
                table: "senders");

            migrationBuilder.DropIndex(
                name: "ix_senders_canonical_domain",
                table: "senders");

            migrationBuilder.DropIndex(
                name: "ix_senders_canonical_domain_trgm",
                table: "senders");

            migrationBuilder.DropIndex(
                name: "ix_messages_canonical_address",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "canonical_address",
                table: "senders");

            migrationBuilder.DropColumn(
                name: "canonical_domain",
                table: "senders");

            migrationBuilder.DropColumn(
                name: "is_relay",
                table: "senders");

            migrationBuilder.DropColumn(
                name: "canonical_address",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "canonical_domain",
                table: "messages");
        }
    }
}
