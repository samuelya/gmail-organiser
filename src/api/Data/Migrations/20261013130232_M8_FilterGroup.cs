using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M8_FilterGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "group_id",
                table: "filters",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_filters_group_id",
                table: "filters",
                column: "group_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_filters_group_id",
                table: "filters");

            migrationBuilder.DropColumn(
                name: "group_id",
                table: "filters");
        }
    }
}
