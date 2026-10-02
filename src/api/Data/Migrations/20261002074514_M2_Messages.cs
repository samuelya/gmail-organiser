using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M2_Messages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fetch_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    account_email = table.Column<string>(type: "text", nullable: true),
                    mailbox_phase = table.Column<string>(type: "text", nullable: false),
                    page_token = table.Column<string>(type: "text", nullable: true),
                    inbox_fetched = table.Column<int>(type: "integer", nullable: false),
                    all_mail_fetched = table.Column<int>(type: "integer", nullable: false),
                    messages_total = table.Column<long>(type: "bigint", nullable: true),
                    last_history_id = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fetch_state", x => x.id);
                    table.CheckConstraint("ck_fetch_state_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    thread_id = table.Column<string>(type: "text", nullable: false),
                    history_id = table.Column<string>(type: "text", nullable: true),
                    from_address = table.Column<string>(type: "text", nullable: false),
                    from_name = table.Column<string>(type: "text", nullable: true),
                    to_header = table.Column<string>(type: "text", nullable: true),
                    subject = table.Column<string>(type: "text", nullable: true),
                    internal_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    label_ids = table.Column<string[]>(type: "text[]", nullable: false),
                    category = table.Column<string>(type: "text", nullable: true),
                    has_attachment = table.Column<bool>(type: "boolean", nullable: false),
                    size_estimate = table.Column<int>(type: "integer", nullable: false),
                    snippet = table.Column<string>(type: "text", nullable: true),
                    list_id = table.Column<string>(type: "text", nullable: true),
                    list_unsubscribe = table.Column<string>(type: "text", nullable: true),
                    analysis_status = table.Column<string>(type: "text", nullable: false, defaultValue: "not_analysed"),
                    deleted_in_gmail = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "senders",
                columns: table => new
                {
                    address = table.Column<string>(type: "text", nullable: false),
                    domain = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    total_count = table.Column<int>(type: "integer", nullable: false),
                    analysed_count = table.Column<int>(type: "integer", nullable: false),
                    applied_count = table.Column<int>(type: "integer", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    allowlisted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_senders", x => x.address);
                });

            migrationBuilder.InsertData(
                table: "fetch_state",
                columns: new[] { "id", "account_email", "all_mail_fetched", "completed_at", "inbox_fetched", "last_history_id", "mailbox_phase", "messages_total", "page_token", "started_at", "updated_at" },
                values: new object[] { 1, null, 0, null, 0, null, "not_started", null, null, null, new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.CreateIndex(
                name: "ix_messages_analysis_status",
                table: "messages",
                column: "analysis_status");

            migrationBuilder.CreateIndex(
                name: "ix_messages_from_address",
                table: "messages",
                column: "from_address");

            migrationBuilder.CreateIndex(
                name: "ix_messages_internal_date",
                table: "messages",
                column: "internal_date");

            migrationBuilder.CreateIndex(
                name: "ix_messages_label_ids",
                table: "messages",
                column: "label_ids")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_senders_domain",
                table: "senders",
                column: "domain");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fetch_state");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "senders");
        }
    }
}
