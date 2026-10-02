using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M3_Analysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "action_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    message_count = table.Column<int>(type: "integer", nullable: false),
                    undo_of = table.Column<Guid>(type: "uuid", nullable: true),
                    undone_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_action_batches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "analysis_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scope = table.Column<string>(type: "text", nullable: false),
                    sender_address = table.Column<string>(type: "text", nullable: true),
                    message_ids = table.Column<string[]>(type: "text[]", nullable: true),
                    requested_count = table.Column<int>(type: "integer", nullable: false),
                    grouping_mode = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    messages_covered = table.Column<int>(type: "integer", nullable: false),
                    messages_llm = table.Column<int>(type: "integer", nullable: false),
                    messages_derived = table.Column<int>(type: "integer", nullable: false),
                    messages_from_memory = table.Column<int>(type: "integer", nullable: false),
                    llm_calls = table.Column<int>(type: "integer", nullable: false),
                    groups = table.Column<int>(type: "integer", nullable: false),
                    mixed_groups = table.Column<int>(type: "integer", nullable: false),
                    failed_messages = table.Column<int>(type: "integer", nullable: false),
                    attachments_converted = table.Column<int>(type: "integer", nullable: false),
                    attachments_skipped = table.Column<int>(type: "integer", nullable: false),
                    model = table.Column<string>(type: "text", nullable: true),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_analysis_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<string>(type: "text", nullable: true),
                    sender_address = table.Column<string>(type: "text", nullable: false),
                    list_id = table.Column<string>(type: "text", nullable: true),
                    subject_template = table.Column<string>(type: "text", nullable: true),
                    topic_label = table.Column<string>(type: "text", nullable: false),
                    needs_action = table.Column<bool>(type: "boolean", nullable: false),
                    to_be_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    edited = table.Column<bool>(type: "boolean", nullable: false),
                    embedding = table.Column<Vector>(type: "vector", nullable: true),
                    embedding_model = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "action_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<string>(type: "text", nullable: false),
                    suggestion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    labels_added = table.Column<string[]>(type: "text[]", nullable: false),
                    labels_removed = table.Column<string[]>(type: "text[]", nullable: false),
                    label_ids_before = table.Column<string[]>(type: "text[]", nullable: false),
                    label_ids_after = table.Column<string[]>(type: "text[]", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    undone_by_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_action_log", x => x.id);
                    table.ForeignKey(
                        name: "fk_action_log_action_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "action_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "suggestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<string>(type: "text", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sender_address = table.Column<string>(type: "text", nullable: false),
                    group_key = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    topic_label = table.Column<string>(type: "text", nullable: false),
                    is_new_label = table.Column<bool>(type: "boolean", nullable: false),
                    needs_action = table.Column<bool>(type: "boolean", nullable: false),
                    to_be_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    unsubscribe_suggested = table.Column<bool>(type: "boolean", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    filter_criteria = table.Column<string>(type: "jsonb", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    edited = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suggestions", x => x.id);
                    table.CheckConstraint("ck_suggestions_confidence", "confidence BETWEEN 0 AND 1");
                    table.ForeignKey(
                        name: "fk_suggestions_analysis_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "analysis_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_suggestions_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_action_batches_created_at",
                table: "action_batches",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_action_log_batch_id",
                table: "action_log",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_action_log_message_id",
                table: "action_log",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_analysis_runs_status_created_at",
                table: "analysis_runs",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_decisions_embedding_model_sender_address",
                table: "decisions",
                columns: new[] { "embedding_model", "sender_address" });

            migrationBuilder.CreateIndex(
                name: "ix_decisions_sender_address_outcome_created_at",
                table: "decisions",
                columns: new[] { "sender_address", "outcome", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_suggestions_message_id",
                table: "suggestions",
                column: "message_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_suggestions_run_id",
                table: "suggestions",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_suggestions_sender_address_status",
                table: "suggestions",
                columns: new[] { "sender_address", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_suggestions_status_confidence",
                table: "suggestions",
                columns: new[] { "status", "confidence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The pre-M3 AnalysisStatus has no approved/rejected; fold them back so old code can read every row.
            migrationBuilder.Sql("UPDATE messages SET analysis_status = 'analysed' WHERE analysis_status IN ('approved', 'rejected');");

            migrationBuilder.DropTable(
                name: "action_log");

            migrationBuilder.DropTable(
                name: "decisions");

            migrationBuilder.DropTable(
                name: "suggestions");

            migrationBuilder.DropTable(
                name: "action_batches");

            migrationBuilder.DropTable(
                name: "analysis_runs");
        }
    }
}
