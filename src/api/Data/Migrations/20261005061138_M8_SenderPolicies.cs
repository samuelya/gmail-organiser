using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M8_SenderPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sender_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    scope_key = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    is_mixed = table.Column<bool>(type: "boolean", nullable: false),
                    topic_label = table.Column<string>(type: "text", nullable: true),
                    document_type_label = table.Column<string>(type: "text", nullable: true),
                    mail_type = table.Column<string>(type: "text", nullable: true),
                    retention_days = table.Column<int>(type: "integer", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: true),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    edited = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sender_policies", x => x.id);
                    table.CheckConstraint("ck_sender_policies_confidence", "confidence BETWEEN 0 AND 1");
                    table.CheckConstraint("ck_sender_policies_retention", "retention_days IS NULL OR retention_days > 0");
                    table.CheckConstraint("ck_sender_policies_topic", "is_mixed OR topic_label IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_sender_policies_analysis_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "analysis_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "sender_policy_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    topic_label = table.Column<string>(type: "text", nullable: false),
                    document_type_label = table.Column<string>(type: "text", nullable: true),
                    mail_type = table.Column<string>(type: "text", nullable: true),
                    retention_days = table.Column<int>(type: "integer", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    match = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sender_policy_rules", x => x.id);
                    table.CheckConstraint("ck_sender_policy_rules_match", "jsonb_strip_nulls(match) <> '{}'::jsonb");
                    table.CheckConstraint("ck_sender_policy_rules_retention", "retention_days IS NULL OR retention_days > 0");
                    table.ForeignKey(
                        name: "fk_sender_policy_rules_sender_policies_policy_id",
                        column: x => x.policy_id,
                        principalTable: "sender_policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sender_policies_run_id",
                table: "sender_policies",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_sender_policies_scope_scope_key",
                table: "sender_policies",
                columns: new[] { "scope", "scope_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sender_policies_status",
                table: "sender_policies",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_sender_policy_rules_policy_id_position",
                table: "sender_policy_rules",
                columns: new[] { "policy_id", "position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sender_policy_rules");

            migrationBuilder.DropTable(
                name: "sender_policies");
        }
    }
}
