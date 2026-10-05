using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Common;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Policies;
using GmailOrganiser.Rules;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Integration;
/// <summary>
/// The fixture of the policy filter proposal tests (#373): a fake-Gmail host with a synthetic delete label over a reset
/// database, and the synthetic senders, messages, policies, rules and filters they seed.
/// </summary>
public abstract class PolicyFilterProposalTestBase(ApiFactory factory, PostgresFixture postgres) : IAsyncLifetime
{
    protected const string DeleteLabel = "Synthetic Delete";
    protected const string ListId = "digest.example.com";
    protected static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected PostgresFixture Postgres { get; } = postgres;

    protected WebApplicationFactory<Program> Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Postgres.ResetFetchStateAsync();
        await using (var db = Postgres.CreateDbContext())
        {
            await db.ActionLog.ExecuteDeleteAsync(Ct);
            await db.ActionBatches.ExecuteDeleteAsync(Ct);
            await db.Filters.ExecuteDeleteAsync(Ct);
            await db.SenderPolicies.ExecuteDeleteAsync(Ct);
            await db.Suggestions.ExecuteDeleteAsync(Ct);
            await db.Messages.ExecuteDeleteAsync(Ct);
            await db.Senders.ExecuteDeleteAsync(Ct);
            await db.Settings.ExecuteDeleteAsync(Ct);
        }

        Host = factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true"));
        await using var scope = Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(s => s with { DeleteLabelName = DeleteLabel }, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await using var db = Postgres.CreateDbContext();
        await db.SenderPolicies.ExecuteDeleteAsync(CancellationToken.None);
        await db.Settings.ExecuteDeleteAsync(CancellationToken.None);
    }

    protected List<string> Keywords() => Host.Services.GetRequiredService<IOptions<PolicyOptions>>().Value.TransactionalKeywords;

    protected string Negation() => string.Join(' ', Keywords().Select(k => "-" + k).Prepend("-has:attachment"));

    protected async Task<PagedDto<FilterProposalDto>> ProposalsAsync(string source, WebApplicationFactory<Program>? app = null) =>
        (await (app ?? Host).CreateClient().GetFromJsonAsync<PagedDto<FilterProposalDto>>($"/api/rules/filters/proposals?source={source}", Ct))
            .ShouldNotBeNull();

    protected static SenderRow Sender(string address, string canonical) => new()
    {
        Address = address,
        Domain = address[(address.IndexOf('@') + 1)..],
        CanonicalAddress = canonical,
        CanonicalDomain = canonical[(canonical.IndexOf('@') + 1)..],
        TotalCount = 2,
        UpdatedAt = Now,
    };

    protected static MessageRow Message(string id, string from, string? listId) => new()
    {
        Id = id,
        ThreadId = $"t-{id}",
        FromAddress = from,
        CanonicalAddress = from,
        CanonicalDomain = from[(from.IndexOf('@') + 1)..],
        Subject = "Synthetic subject",
        ListId = listId,
        InternalDate = Now,
        LabelIds = ["INBOX"],
        FetchedAt = Now,
        UpdatedAt = Now,
    };

    protected static SenderPolicyRow Policy(PolicyScope scope, string key, PolicyAction action, string? topic) => new()
    {
        Id = Guid.NewGuid(),
        Scope = scope,
        ScopeKey = key,
        TopicLabel = topic ?? "Synthetic/Topic",
        Action = action,
        Confidence = 0.9,
        Reason = "Synthetic reason",
        Status = PolicyStatus.Approved,
        CreatedAt = Now,
    };

    protected static SenderPolicyRuleRow Rule(Guid policyId, int position, RuleMatch match, PolicyAction action, string topic = "Synthetic/Topic") => new()
    {
        Id = Guid.NewGuid(),
        PolicyId = policyId,
        Position = position,
        Name = $"Synthetic rule {position}",
        Match = match,
        TopicLabel = topic,
        Action = action,
        Status = PolicyStatus.Approved,
        Source = PolicyRuleSource.User,
        Reason = "Synthetic reason",
        CreatedAt = Now,
    };

    protected static FilterRow Filter(string id, GmailFilterCriteria criteria) => new()
    {
        Id = id,
        Criteria = FilterRow.WriteCriteria(criteria),
        CriteriaSummary = "synthetic",
        FirstSeenAt = Now,
        LastSeenAt = Now,
        UpdatedAt = Now,
    };
}
