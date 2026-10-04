using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M6_LabelPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "label_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    items = table.Column<string>(type: "jsonb", nullable: false),
                    warnings = table.Column<List<string>>(type: "text[]", nullable: false),
                    label_count = table.Column<int>(type: "integer", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_label_plans", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_label_plans_created_at",
                table: "label_plans",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ux_label_plans_draft",
                table: "label_plans",
                column: "status",
                unique: true,
                filter: "status = 'draft'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "label_plans");
        }
    }
}
