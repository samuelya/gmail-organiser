using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M8_SenderStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "bulk_header_count",
                table: "senders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "first_seen_at",
                table: "senders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "forums_count",
                table: "senders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "senders",
                type: "text",
                nullable: false,
                defaultValue: "unknown");

            migrationBuilder.AddColumn<int>(
                name: "list_unsubscribe_count",
                table: "senders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "primary_count",
                table: "senders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "promotions_count",
                table: "senders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "replied_count",
                table: "senders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "social_count",
                table: "senders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "starred_count",
                table: "senders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "stats_at",
                table: "senders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "unread_count",
                table: "senders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "updates_count",
                table: "senders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_senders_kind",
                table: "senders",
                column: "kind");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_senders_kind",
                table: "senders");

            migrationBuilder.DropColumn(
                name: "bulk_header_count",
                table: "senders");

            migrationBuilder.DropColumn(
                name: "first_seen_at",
                table: "senders");

            migrationBuilder.DropColumn(
                name: "forums_count",
                table: "senders");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "senders");

            migrationBuilder.DropColumn(
                name: "list_unsubscribe_count",
                table: "senders");

            migrationBuilder.DropColumn(
                name: "primary_count",
                table: "senders");

            migrationBuilder.DropColumn(
                name: "promotions_count",
                table: "senders");

            migrationBuilder.DropColumn(
                name: "replied_count",
                table: "senders");

            migrationBuilder.DropColumn(
                name: "social_count",
                table: "senders");

            migrationBuilder.DropColumn(
                name: "starred_count",
                table: "senders");

            migrationBuilder.DropColumn(
                name: "stats_at",
                table: "senders");

            migrationBuilder.DropColumn(
                name: "unread_count",
                table: "senders");

            migrationBuilder.DropColumn(
                name: "updates_count",
                table: "senders");
        }
    }
}
