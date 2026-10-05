using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Memory;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Policies;

/// <summary>
/// The approved policies of a set of messages, loaded in one query by key set. A message's policy is its canonical
/// sender's, else its List-Id's, else its canonical domain's (DESIGN §6.2).
/// </summary>
public sealed class PolicyLookup
{
    public static readonly PolicyLookup Empty = new([]);

    private readonly Dictionary<(PolicyScope, string), SenderPolicyRow> policies;

    private PolicyLookup(Dictionary<(PolicyScope, string), SenderPolicyRow> policies) => this.policies = policies;

    public bool IsEmpty => policies.Count == 0;

    public static async Task<PolicyLookup> LoadAsync(AppDbContext db, IReadOnlyCollection<MessageRow> messages, CancellationToken ct)
    {
        var senders = messages.Select(m => m.CanonicalAddress).Where(k => k != "").Distinct().ToArray();
        var domains = messages.Select(m => m.CanonicalDomain).Where(k => k != "").Distinct().ToArray();
        var lists = messages.Select(m => GroupKey.NormaliseListId(m.ListId)).OfType<string>().Distinct().ToArray();
        if (senders.Length + domains.Length + lists.Length == 0)
        {
            return Empty;
        }

        var rows = await db.SenderPolicies.AsNoTracking().Include(p => p.Rules)
            .Where(p => p.Status == PolicyStatus.Approved
                && ((p.Scope == PolicyScope.Sender && senders.Contains(p.ScopeKey))
                    || (p.Scope == PolicyScope.List && lists.Contains(p.ScopeKey))
                    || (p.Scope == PolicyScope.Domain && domains.Contains(p.ScopeKey))))
            .ToListAsync(ct);
        return new PolicyLookup(rows.ToDictionary(p => (p.Scope, p.ScopeKey)));
    }

    /// <summary>The policy that decides <paramref name="m"/>, or null.</summary>
    public SenderPolicyRow? For(MessageRow m) =>
        policies.GetValueOrDefault((PolicyScope.Sender, m.CanonicalAddress))
        ?? (GroupKey.NormaliseListId(m.ListId) is { } list ? policies.GetValueOrDefault((PolicyScope.List, list)) : null)
        ?? policies.GetValueOrDefault((PolicyScope.Domain, m.CanonicalDomain));

    /// <summary>The policy of each message that has one, once per policy, for the analysis prompt.</summary>
    public IReadOnlyList<SenderPolicyHint> HintsFor(IEnumerable<MessageRow> messages) =>
        [.. messages.Select(For).OfType<SenderPolicyRow>().DistinctBy(p => p.Id)
            .Select(p => new SenderPolicyHint(p.Scope.ToString().ToLowerInvariant(), p.ScopeKey, p.IsMixed, p.TopicLabel, p.MailType,
                p.Action.ToString().ToLowerInvariant()))];

    /// <summary>
    /// <paramref name="query"/> without messages whose policy (same precedence as <see cref="For"/>) is approved and not
    /// mixed: such a policy decides every message, so the LLM never needs to see it.
    /// </summary>
    public static IQueryable<MessageRow> WithoutSingleLabelPolicy(AppDbContext db, IQueryable<MessageRow> query)
    {
        var approved = db.SenderPolicies.Where(p => p.Status == PolicyStatus.Approved);
        return query.Where(m => approved
            .Where(p => (p.Scope == PolicyScope.Sender && p.ScopeKey == m.CanonicalAddress)
                || (p.Scope == PolicyScope.List && m.ListId != null && p.ScopeKey == m.ListId.Trim().ToLower())
                || (p.Scope == PolicyScope.Domain && p.ScopeKey == m.CanonicalDomain))
            .OrderBy(p => p.Scope == PolicyScope.Sender ? 0 : p.Scope == PolicyScope.List ? 1 : 2)
            .Select(p => (bool?)p.IsMixed)
            .FirstOrDefault() != false);
    }
}

/// <summary>
/// Covers newly fetched mail with the approved sender policies (#360, DESIGN §6.1): each matching message gets an approved
/// <see cref="SuggestionSource.Policy"/> suggestion, as <see cref="PolicyApplyJob"/> would make it, and one
/// <see cref="ApplyActionsJob"/> per chunk applies them. Never calls Gmail itself. Runs in the caller's transaction, so the
/// fetched rows, their suggestions and the apply job commit together; the caller calls <see cref="Committed"/> after.
/// </summary>
public sealed class PolicyCoverage(
    AppDbContext db,
    PolicyMatcher matcher,
    ApplyService apply,
    DecisionRecorder decisions,
    SenderStatsUpdater stats,
    ISettingsStore settingsStore,
    TimeProvider time)
{
    /// <param name="newMessages">Tracked rows the caller just inserted; a message with a suggestion already is left alone.</param>
    /// <returns>How many messages got a policy suggestion.</returns>
    public async Task<int> CoverAsync(IReadOnlyList<MessageRow> newMessages, CancellationToken ct)
    {
        var live = newMessages.Where(m => !m.DeletedInGmail).ToList();
        if (live.Count == 0)
        {
            return 0;
        }

        var settings = await settingsStore.GetAsync(ct);
        if (!settings.PolicyAutoApplyFetched)
        {
            return 0;
        }

        var lookup = await PolicyLookup.LoadAsync(db, live, ct);
        if (lookup.IsEmpty)
        {
            return 0;
        }

        var ids = live.ConvertAll(m => m.Id);
        var suggested = await db.Suggestions.AsNoTracking().Where(s => ids.Contains(s.MessageId)).Select(s => s.MessageId)
            .ToHashSetAsync(StringComparer.Ordinal, ct);
        var matched = live
            .Where(m => !suggested.Contains(m.Id))
            .Select(m => (Message: m, Policy: lookup.For(m)))
            .Select(x => (x.Message, x.Policy, Match: x.Policy is null ? null : matcher.Match(x.Message, x.Policy, x.Message.CanonicalAddress)))
            .Where(x => x.Match is not null)
            .ToList();
        if (matched.Count == 0)
        {
            return 0;
        }

        var allowlist = await AllowlistLoader.LoadAsync(db, settings, [.. matched.Select(x => x.Message.FromAddress).Distinct()], ct);
        var now = time.GetUtcNow();
        var rows = new List<SuggestionRow>(matched.Count);
        foreach (var (message, policy, match) in matched)
        {
            var row = new SuggestionRow { Id = Guid.CreateVersion7(now), MessageId = message.Id, CreatedAt = now };
            PolicyApplyJob.Fill(row, message, policy!, match!, MessageProtection.Reason(message, allowlist, settings.Protection));
            row.SetStatus(SuggestionStatus.Approved, message, now);
            db.Suggestions.Add(row);
            await decisions.RecordAsync(row, message, DecisionOutcome.Approved, ct);
            rows.Add(row);
        }

        await db.SaveChangesAsync(ct);
        await stats.UpdateAnalysedCountsAsync(matched.Select(x => x.Message.FromAddress), ct);
        await apply.StartAsync(
            ActionKind.Apply, null, [.. rows.Select(r => r.Id)], $"Apply policies to {rows.Count} fetched message(s)", ct);
        return rows.Count;
    }

    /// <summary>Wakes the decision embedding; call after the caller's transaction commits.</summary>
    public void Committed() => decisions.Committed();
}
