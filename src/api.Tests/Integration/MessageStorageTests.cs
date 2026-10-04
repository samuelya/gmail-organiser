using GmailOrganiser.Fetch;
using GmailOrganiser.Senders;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class MessageStorageTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Messages.ExecuteDeleteAsync(Ct);
        await db.Senders.ExecuteDeleteAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Message_and_sender_round_trip()
    {
        var internalDate = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var fetchedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        await using (var db = postgres.CreateDbContext())
        {
            db.Messages.Add(new MessageRow
            {
                Id = "msg-1",
                ThreadId = "thread-1",
                HistoryId = "1001",
                FromAddress = "news@example.com",
                FromName = "Example News",
                ToHeader = "user@example.com",
                Subject = "Synthetic subject",
                InternalDate = internalDate,
                LabelIds = ["INBOX", "UNREAD", "CATEGORY_UPDATES"],
                Category = MessageCategory.Updates,
                HasAttachment = true,
                SizeEstimate = 2048,
                Snippet = "Synthetic snippet",
                ListId = "<news.example.com>",
                ListUnsubscribe = "<https://example.com/unsubscribe>",
                FetchedAt = fetchedAt,
                UpdatedAt = fetchedAt,
            });
            db.Senders.Add(new SenderRow
            {
                Address = "news@example.com",
                Domain = "example.com",
                DisplayName = "Example News",
                TotalCount = 1,
                LastSeenAt = internalDate,
                UpdatedAt = fetchedAt,
            });
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = postgres.CreateDbContext())
        {
            var message = await db.Messages.AsNoTracking().SingleAsync(m => m.Id == "msg-1", Ct);
            message.LabelIds.ShouldBe(["INBOX", "UNREAD", "CATEGORY_UPDATES"]);
            message.Category.ShouldBe(MessageCategory.Updates);
            message.AnalysisStatus.ShouldBe(AnalysisStatus.NotAnalysed);
            message.DeletedInGmail.ShouldBeFalse();
            message.InternalDate.ShouldBe(internalDate);
            message.InternalDate.Offset.ShouldBe(TimeSpan.Zero);
            message.FetchedAt.ShouldBe(fetchedAt);
            message.SizeEstimate.ShouldBe(2048);

            var sender = await db.Senders.AsNoTracking().SingleAsync(s => s.Address == "news@example.com", Ct);
            sender.Domain.ShouldBe("example.com");
            sender.TotalCount.ShouldBe(1);
            sender.Allowlisted.ShouldBeFalse();
            sender.LastSeenAt.ShouldBe(internalDate);
            sender.LastSeenAt!.Value.Offset.ShouldBe(TimeSpan.Zero);

            var byLabel = await db.Messages.CountAsync(m => m.LabelIds.Contains("CATEGORY_UPDATES"), Ct);
            byLabel.ShouldBe(1);
        }
    }

    [Fact]
    public async Task Enum_like_columns_are_stored_as_snake_case_strings()
    {
        await using var db = postgres.CreateDbContext();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO messages (id, thread_id, from_address, internal_date, label_ids, category, has_attachment, size_estimate, fetched_at, updated_at)
            VALUES ('msg-raw', 'thread-raw', 'raw@example.com', now(), ARRAY['INBOX'], 'promotions', FALSE, 1, now(), now())
            """,
            Ct);

        var status = await db.Database
            .SqlQuery<string>($"SELECT analysis_status AS \"Value\" FROM messages WHERE id = 'msg-raw'")
            .SingleAsync(Ct);
        status.ShouldBe("not_analysed");

        var message = await db.Messages.AsNoTracking().SingleAsync(m => m.Id == "msg-raw", Ct);
        message.Category.ShouldBe(MessageCategory.Promotions);
        message.AnalysisStatus.ShouldBe(AnalysisStatus.NotAnalysed);
    }
}
