using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Rules;
using GmailOrganiser.Senders;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Policies;

/// <summary>The policy review list and detail (DESIGN §6.3): read-only, no Gmail mutation and no LLM.</summary>
public sealed class PolicyQuery(AppDbContext db, PolicyMatcher matcher, TransactionalGuard guard, SenderProfileBuilder profiles)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxSearchLength = 200;

    /// <summary>The detail preview runs over at most this many of the scope's newest live messages.</summary>
    public const int PreviewMaxMessages = 2000;
    public const int MaxSampleSubjects = 3;

    /// <summary>Parses the list's query values; <paramref name="errors"/> holds the field errors when it returns null.</summary>
    public static PolicyListQuery? Parse(string? status, string? search, int? page, int? pageSize, out Dictionary<string, string[]> errors)
    {
        errors = [];
        var parsed = PolicyStatus.Proposed;
        if (status is not null && !SnakeCaseEnumConverter<PolicyStatus>.TryFromDb(status, out parsed))
        {
            errors["status"] = [$"Must be one of {SnakeCaseEnumConverter<PolicyStatus>.NamesList}."];
        }

        var p = page ?? 1;
        if (p is < 1 or > SenderQuery.MaxPage)
        {
            errors["page"] = [$"Must be between 1 and {SenderQuery.MaxPage}."];
        }

        var size = pageSize ?? DefaultPageSize;
        if (size is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"Must be between 1 and {MaxPageSize}."];
        }

        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        if (term is { Length: > MaxSearchLength } || (term is not null && term.Any(char.IsControl)))
        {
            errors["search"] = [$"Must be at most {MaxSearchLength} characters, without control characters."];
        }

        return errors.Count > 0 ? null : new PolicyListQuery(parsed, term, p, size);
    }

    /// <summary>Policies of one status whose scope key, display name or topic label contains the search term, most mail first.</summary>
    public async Task<PagedDto<SenderPolicyDto>> ListAsync(PolicyListQuery query, CancellationToken ct)
    {
        var policies = db.SenderPolicies.AsNoTracking().Where(p => p.Status == query.Status);
        if (query.Search is { } search)
        {
            var pattern = $"%{SenderQuery.EscapeLike(search)}%";
            policies = policies.Where(p =>
                EF.Functions.ILike(p.ScopeKey, pattern, "\\")
                || (p.DisplayName != null && EF.Functions.ILike(p.DisplayName, pattern, "\\"))
                || (p.TopicLabel != null && EF.Functions.ILike(p.TopicLabel, pattern, "\\")));
        }

        var total = await policies.LongCountAsync(ct);
        var items = await SummariesAsync(policies, (query.Page - 1) * query.PageSize, query.PageSize, ct);
        return new PagedDto<SenderPolicyDto>(items, query.Page, query.PageSize, total);
    }

    /// <summary>One policy with its rules and their preview; null when there is no such policy.</summary>
    public async Task<SenderPolicyDetailDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var policy = await db.SenderPolicies.AsNoTracking().Include(p => p.Rules).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null)
        {
            return null;
        }

        var summary = await GetSummaryAsync(id, ct) ?? throw new InvalidOperationException($"Policy {id} vanished while read.");
        var messages = await SenderProfileBuilder.InScope(db, policy.Scope, policy.ScopeKey)
            .OrderByDescending(m => m.InternalDate).ThenBy(m => m.Id)
            .Take(PreviewMaxMessages + 1)
            .ToListAsync(ct);
        var sampled = messages.Count > PreviewMaxMessages;
        if (sampled)
        {
            messages.RemoveAt(PreviewMaxMessages);
        }

        var preview = Preview(policy, messages);
        var profile = await profiles.BuildAsync(policy.Scope, policy.ScopeKey, includeBodies: false, ct);
        var rules = policy.Rules.OrderBy(r => r.Position).Select(r =>
        {
            var hits = preview.Rules.GetValueOrDefault(r.Id);
            return ToDto(r, hits?.Count ?? 0, hits?.Subjects ?? []);
        }).ToList();

        return new SenderPolicyDetailDto(
            summary, rules, preview.Default, preview.Unmatched, preview.Guarded, messages.Count, sampled,
            profile is null ? [] : [.. profile.Templates.Select(SenderTemplateDto.From)]);
    }

    /// <summary>The list row of one policy, without the preview.</summary>
    public async Task<SenderPolicyDto?> GetSummaryAsync(Guid id, CancellationToken ct) =>
        (await SummariesAsync(db.SenderPolicies.AsNoTracking().Where(p => p.Id == id), 0, 1, ct)).SingleOrDefault();

    private PreviewCounts Preview(SenderPolicyRow policy, List<MessageRow> messages)
    {
        // An untracked copy in which every rule not rejected matches, in saved order, as it will once approved.
        var copy = new SenderPolicyRow
        {
            IsMixed = policy.IsMixed,
            TopicLabel = policy.TopicLabel,
            DocumentTypeLabel = policy.DocumentTypeLabel,
            MailType = policy.MailType,
            RetentionDays = policy.RetentionDays,
            Action = policy.Action,
            Rules = policy.Rules.ConvertAll(r => new SenderPolicyRuleRow
            {
                Id = r.Id,
                Position = r.Position,
                Match = r.Match,
                TopicLabel = r.TopicLabel,
                DocumentTypeLabel = r.DocumentTypeLabel,
                MailType = r.MailType,
                RetentionDays = r.RetentionDays,
                Action = r.Action,
                Status = r.Status == PolicyStatus.Rejected ? PolicyStatus.Rejected : PolicyStatus.Approved,
            }),
        };

        var counts = new PreviewCounts();
        foreach (var m in messages)
        {
            var match = matcher.Match(m, copy, m.CanonicalAddress);
            if (match is null)
            {
                // A mixed sender: no rule matched, or the guard stopped a delete; both go to review.
                if (policy.IsMixed && guard.IsTransactional(m))
                {
                    counts.Guarded++;
                }
                else
                {
                    counts.Unmatched++;
                }
            }
            else if (match.Guarded)
            {
                counts.Guarded++;
            }
            else if (match.Rule is { } rule)
            {
                if (!counts.Rules.TryGetValue(rule.Id, out var hits))
                {
                    counts.Rules[rule.Id] = hits = new RuleHits();
                }

                hits.Count++;
                if (hits.Subjects.Count < MaxSampleSubjects)
                {
                    hits.Subjects.Add(m.Subject ?? "");
                }
            }
            else
            {
                counts.Default++;
            }
        }

        return counts;
    }

    /// <summary>
    /// The list rows in <see cref="SenderPolicyDto.MessageCount"/> order (then scope key), with the kind and unread ratio
    /// of the senders rows in scope; a list's counts come from its live messages.
    /// </summary>
    private async Task<List<SenderPolicyDto>> SummariesAsync(IQueryable<SenderPolicyRow> policies, int skip, int take, CancellationToken ct)
    {
        var page = await policies
            .Select(p => new
            {
                Policy = p,
                RuleCount = p.Rules.Count,
                MessageCount = p.Scope == PolicyScope.List
                    ? db.Messages.Count(m => !m.DeletedInGmail && m.ListId != null && m.ListId.Trim().ToLower() == p.ScopeKey)
                    : db.Senders
                        .Where(s => p.Scope == PolicyScope.Sender ? s.CanonicalAddress == p.ScopeKey : s.CanonicalDomain == p.ScopeKey)
                        .Sum(s => s.TotalCount),
            })
            .OrderByDescending(x => x.MessageCount).ThenBy(x => x.Policy.ScopeKey).ThenBy(x => x.Policy.Id)
            .Skip(skip).Take(take)
            .ToListAsync(ct);
        if (page.Count == 0)
        {
            return [];
        }

        var addresses = page.Where(x => x.Policy.Scope == PolicyScope.Sender).Select(x => x.Policy.ScopeKey).ToList();
        var domains = page.Where(x => x.Policy.Scope == PolicyScope.Domain).Select(x => x.Policy.ScopeKey).ToList();
        var lists = page.Where(x => x.Policy.Scope == PolicyScope.List).Select(x => x.Policy.ScopeKey).ToList();
        var senders = addresses.Count + domains.Count == 0 ? [] : await db.Senders.AsNoTracking()
            .Where(s => addresses.Contains(s.CanonicalAddress) || domains.Contains(s.CanonicalDomain))
            .ToListAsync(ct);
        var listUnread = lists.Count == 0 ? [] : await db.Messages.AsNoTracking()
            .Where(m => !m.DeletedInGmail && m.ListId != null && lists.Contains(m.ListId.Trim().ToLower())
                && m.LabelIds.Contains(FilterSpec.Unread))
            .GroupBy(m => m.ListId!.Trim().ToLower())
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        return page.ConvertAll(x =>
        {
            var p = x.Policy;
            var (kind, unread) = p.Scope switch
            {
                PolicyScope.Sender => Engagement(senders.Where(s => s.CanonicalAddress == p.ScopeKey).ToList()),
                PolicyScope.Domain => Engagement(senders.Where(s => s.CanonicalDomain == p.ScopeKey).ToList()),
                _ => (x.MessageCount == 0 ? SenderKind.Unknown : SenderKind.Bulk, listUnread.GetValueOrDefault(p.ScopeKey)),
            };
            return new SenderPolicyDto(
                p.Id,
                SnakeCaseEnumConverter<PolicyScope>.ToDb(p.Scope),
                p.ScopeKey,
                p.DisplayName,
                p.IsMixed,
                p.TopicLabel,
                p.DocumentTypeLabel,
                p.MailType is { } t ? SnakeCaseEnumConverter<Analysis.MailType>.ToDb(t) : null,
                p.RetentionDays,
                SnakeCaseEnumConverter<PolicyAction>.ToDb(p.Action),
                p.Confidence,
                p.Reason,
                p.Model,
                p.PromptVersion,
                SnakeCaseEnumConverter<PolicyStatus>.ToDb(p.Status),
                p.Edited,
                p.CreatedAt,
                p.DecidedAt,
                p.AppliedAt,
                x.RuleCount,
                x.MessageCount,
                SnakeCaseEnumConverter<SenderKind>.ToDb(kind),
                x.MessageCount == 0 ? 0 : Math.Round((double)unread / x.MessageCount, 3));
        });
    }

    /// <summary>
    /// One row's own kind; for several rows (a relay, a domain) the kind of their summed counts, where the not stored
    /// bulk-or-list-unsubscribe count is taken as the larger of the two.
    /// </summary>
    private static (SenderKind Kind, int Unread) Engagement(List<SenderRow> rows)
    {
        if (rows.Count == 1)
        {
            return (rows[0].Kind, rows[0].UnreadCount);
        }

        var c = new SenderCounts(
            rows.Sum(r => r.TotalCount),
            rows.Sum(r => r.UnreadCount),
            rows.Sum(r => r.RepliedCount),
            rows.Sum(r => r.StarredCount),
            rows.Sum(r => r.ListUnsubscribeCount),
            rows.Sum(r => r.BulkHeaderCount),
            rows.Sum(r => r.PrimaryCount),
            rows.Sum(r => r.PromotionsCount),
            rows.Sum(r => r.SocialCount),
            rows.Sum(r => r.UpdatesCount),
            rows.Sum(r => r.ForumsCount));
        return (SenderStatsCalculator.Kind(c with { BulkOrListUnsubscribe = Math.Max(c.ListUnsubscribe, c.BulkHeader) }), c.Unread);
    }

    internal static PolicyRuleDto ToDto(SenderPolicyRuleRow r, int matchCount, IReadOnlyList<string> sampleSubjects) => new(
        r.Id,
        r.Position,
        r.Name,
        RuleMatchDto.From(r.Match),
        r.TopicLabel,
        r.DocumentTypeLabel,
        r.MailType is { } t ? SnakeCaseEnumConverter<Analysis.MailType>.ToDb(t) : null,
        r.RetentionDays,
        SnakeCaseEnumConverter<PolicyAction>.ToDb(r.Action),
        SnakeCaseEnumConverter<PolicyStatus>.ToDb(r.Status),
        SnakeCaseEnumConverter<PolicyRuleSource>.ToDb(r.Source),
        r.Reason,
        matchCount,
        sampleSubjects);

    private sealed class PreviewCounts
    {
        public int Default { get; set; }
        public int Unmatched { get; set; }
        public int Guarded { get; set; }
        public Dictionary<Guid, RuleHits> Rules { get; } = [];
    }

    private sealed class RuleHits
    {
        public int Count { get; set; }
        public List<string> Subjects { get; } = [];
    }
}

/// <param name="Search">Trimmed; null for all.</param>
public sealed record PolicyListQuery(PolicyStatus Status, string? Search, int Page, int PageSize);
