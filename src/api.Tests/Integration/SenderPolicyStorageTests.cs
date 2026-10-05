using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Policies;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>The <c>M8_SenderPolicies</c> schema (#354): round trip with jsonb match, unique scope key, cascade, checks.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SenderPolicyStorageTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.SenderPolicies.ExecuteDeleteAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Policy_and_rules_round_trip()
    {
        var policy = Policy("shop@example.com");
        policy.Rules.Add(Rule(new RuleMatch { Category = MessageCategory.Promotions, SubjectTemplate = "sale #" }));
        await using (var db = postgres.CreateDbContext())
        {
            db.SenderPolicies.Add(policy);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = postgres.CreateDbContext())
        {
            var stored = await db.SenderPolicies.Include(p => p.Rules).SingleAsync(Ct);
            stored.IsMixed.ShouldBeTrue();
            stored.TopicLabel.ShouldBeNull();
            stored.MailType.ShouldBe(MailType.Marketing);
            var rule = stored.Rules.ShouldHaveSingleItem();
            rule.Match.Category.ShouldBe(MessageCategory.Promotions);
            rule.Match.SubjectTemplate.ShouldBe("sale #");
            rule.Match.ListIdPresent.ShouldBeNull();
            rule.Action.ShouldBe(PolicyAction.Delete);
            rule.Source.ShouldBe(PolicyRuleSource.Llm);

            var raw = await db.Database.SqlQuery<string>($"SELECT mail_type AS \"Value\" FROM sender_policies").SingleAsync(Ct);
            raw.ShouldBe("marketing");
        }
    }

    [Fact]
    public async Task Scope_and_key_are_unique()
    {
        await using var db = postgres.CreateDbContext();
        db.SenderPolicies.Add(Policy("shop@example.com"));
        await db.SaveChangesAsync(Ct);

        db.SenderPolicies.Add(Policy("shop@example.com"));
        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Deleting_a_policy_removes_its_rules()
    {
        var policy = Policy("shop@example.com");
        policy.Rules.Add(Rule(new RuleMatch { ListIdPresent = true }));
        await using (var db = postgres.CreateDbContext())
        {
            db.SenderPolicies.Add(policy);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = postgres.CreateDbContext())
        {
            await db.SenderPolicies.ExecuteDeleteAsync(Ct);
            (await db.SenderPolicyRules.CountAsync(Ct)).ShouldBe(0);
        }
    }

    [Fact]
    public async Task Checks_reject_an_empty_match_and_a_non_mixed_policy_without_a_topic()
    {
        await using (var db = postgres.CreateDbContext())
        {
            var policy = Policy("shop@example.com");
            policy.Rules.Add(Rule(new RuleMatch()));
            db.SenderPolicies.Add(policy);
            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }

        await using (var db = postgres.CreateDbContext())
        {
            var policy = Policy("news@example.com");
            policy.IsMixed = false;
            db.SenderPolicies.Add(policy);
            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }
    }

    private static SenderPolicyRow Policy(string key) => new()
    {
        Id = Guid.NewGuid(),
        Scope = PolicyScope.Sender,
        ScopeKey = key,
        IsMixed = true,
        MailType = MailType.Marketing,
        Action = PolicyAction.Archive,
        Confidence = 0.8,
        Reason = "Sends promotions and receipts.",
        Status = PolicyStatus.Proposed,
        CreatedAt = Now,
    };

    private static SenderPolicyRuleRow Rule(RuleMatch match) => new()
    {
        Id = Guid.NewGuid(),
        Name = "promotions",
        Match = match,
        TopicLabel = "Shops/Example",
        Action = PolicyAction.Delete,
        Status = PolicyStatus.Proposed,
        Source = PolicyRuleSource.Llm,
        Reason = "Promotions tab.",
        CreatedAt = Now,
    };
}
