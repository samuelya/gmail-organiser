using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GmailOrganiser.Data.Migrations
{
    /// <inheritdoc />
    public partial class M2_FetchTotalsBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The totals a finished phase reports are its fetched counts (MailboxFetchJob.InboxTotalOf).
            migrationBuilder.Sql(
                "UPDATE fetch_state SET inbox_total = inbox_fetched WHERE mailbox_phase IN ('all_mail', 'reconcile', 'completed');");
            migrationBuilder.Sql(
                "UPDATE fetch_state SET all_mail_total = all_mail_fetched WHERE mailbox_phase IN ('reconcile', 'completed');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only backfill; the columns are dropped by M2_FetchTotals.Down.
        }
    }
}
