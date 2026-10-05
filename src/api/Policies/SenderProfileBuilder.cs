using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Review;
using GmailOrganiser.Rules;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Policies;

/// <summary>
/// Builds a <see cref="SenderProfile"/> from the stored messages (DESIGN §6.2). No LLM: the policy and taxonomy prompts
/// and the UI share it. The only Gmail calls are the cached label list and, when asked, at most two message bodies.
/// </summary>
public sealed partial class SenderProfileBuilder(
    AppDbContext db, IGmailClient gmail, LabelCatalog labels, ISettingsStore settings, ILogger<SenderProfileBuilder> logger)
{
    public const int ProfileMaxTemplates = 10;
    public const int ProfileMaxBodies = 2;
    public const int MaxDisplayNames = 3;
    public const int MaxAddresses = 10;
    public const int MaxLabels = 5;
    public const int MaxPolicyHints = 5;

    /// <summary>Templates, names, addresses and labels come from the newest this many live messages.</summary>
    public const int MaxMessages = 2000;

    /// <summary>
    /// The canonical address of a sender known by <paramref name="address"/> (raw or canonical, lower-case), or null
    /// when no stored sender has it.
    /// </summary>
    public async Task<string?> ResolveSenderAsync(string address, CancellationToken ct) =>
        await db.Senders.AsNoTracking()
            .Where(s => s.Address == address)
            .Select(s => s.CanonicalAddress)
            .FirstOrDefaultAsync(ct) is { Length: > 0 } canonical
            ? canonical
            : await db.Senders.AnyAsync(s => s.CanonicalAddress == address, ct) ? address : null;

    /// <summary>The profile of the live messages in scope, or null when there are none.</summary>
    /// <param name="scopeKey">The canonical address, canonical domain or normalised List-Id (<see cref="GroupKey.NormaliseListId"/>).</param>
    /// <exception cref="GmailNotConnectedException">Bodies were asked for and Gmail is not connected.</exception>
    public async Task<SenderProfile?> BuildAsync(PolicyScope scope, string scopeKey, bool includeBodies, CancellationToken ct)
    {
        var messages = InScope(db, scope, scopeKey);
        var recent = await messages
            .OrderByDescending(m => m.InternalDate).ThenBy(m => m.Id)
            .Take(MaxMessages)
            .Select(m => new Recent(
                m.Id, m.FromAddress, m.CanonicalDomain, m.FromName, m.Subject, m.LabelIds, m.Category, m.HasAttachment,
                m.ListId, m.ListUnsubscribe, m.Precedence))
            .ToListAsync(ct);
        if (recent.Count == 0)
        {
            return null;
        }

        var current = await settings.GetAsync(ct);
        var domain = scope == PolicyScope.Domain ? scopeKey : MostCommon(recent.Select(r => r.CanonicalDomain)) ?? "";
        var templates = Templates(recent);
        var shown = templates.Take(ProfileMaxTemplates).ToList();
        var others = templates.Skip(ProfileMaxTemplates).Select(t => t.Template).ToList();

        return new SenderProfile(
            scope,
            scopeKey,
            Ranked(recent.Select(r => r.FromName).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()), MaxDisplayNames),
            Ranked(recent.Select(r => r.FromAddress), MaxAddresses),
            await StatsAsync(messages, recent, domain, current.Protection.AllowlistedDomains, ct),
            shown.ConvertAll(t => t.Template),
            others.Count,
            others.Sum(t => t.Count),
            includeBodies ? await BodiesAsync(shown, current.AnalysisBodyMaxChars, ct) : [],
            await LabelsInUseAsync(recent, ct),
            await PolicyHintsAsync(scope, scopeKey, domain, ct));
    }

    /// <summary>The live messages a policy of <paramref name="scope"/> and <paramref name="key"/> covers.</summary>
    internal static IQueryable<MessageRow> InScope(AppDbContext db, PolicyScope scope, string key)
    {
        var live = db.Messages.AsNoTracking().Where(m => !m.DeletedInGmail);
        return scope switch
        {
            PolicyScope.Sender => live.Where(m => m.CanonicalAddress == key),
            PolicyScope.Domain => live.Where(m => m.CanonicalDomain == key),
            PolicyScope.List => live.Where(m => m.ListId != null && m.ListId.Trim().ToLower() == key),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };
    }

    /// <summary>
    /// Counts per distinct bulk-header combination, so <see cref="BulkSignal.Of"/> decides which are bulk exactly as
    /// <see cref="SenderStatsRebuildJob"/> does; the kind is <see cref="SenderStatsCalculator.Kind"/> of the totals.
    /// </summary>
    private async Task<SenderProfileStats> StatsAsync(
        IQueryable<MessageRow> messages, List<Recent> recent, string domain, IReadOnlyList<string> allowlistedDomains, CancellationToken ct)
    {
        var groups = await messages
            .GroupBy(m => new { m.Precedence, m.AutoSubmitted, m.ListId })
            .Select(g => new HeaderGroup(
                g.Key.Precedence,
                g.Key.AutoSubmitted,
                g.Key.ListId,
                g.Count(),
                g.Count(m => m.LabelIds.Contains(FilterSpec.Unread)),
                g.Count(m => m.LabelIds.Contains(MessageProtection.StarredLabel)),
                g.Count(m => m.ListUnsubscribe != null && m.ListUnsubscribe.Trim() != ""),
                g.Count(m => m.Category == MessageCategory.Primary),
                g.Count(m => m.Category == MessageCategory.Promotions),
                g.Count(m => m.Category == MessageCategory.Social),
                g.Count(m => m.Category == MessageCategory.Updates),
                g.Count(m => m.Category == MessageCategory.Forums),
                g.Min(m => m.InternalDate),
                g.Max(m => m.InternalDate)))
            .ToListAsync(ct);
        var replied = await messages.CountAsync(
            m => m.ThreadReplied == true || db.Messages.Any(
                r => r.ThreadId == m.ThreadId && !r.DeletedInGmail && r.LabelIds.Contains(MessageProtection.SentLabel)),
            ct);

        var c = new SenderCounts(0, Replied: replied);
        foreach (var g in groups)
        {
            var bulk = BulkSignal.Of(new MessageRow { Precedence = g.Precedence, AutoSubmitted = g.AutoSubmitted, ListId = g.ListId }) == MessageOrigin.Bulk;
            c = c with
            {
                Total = c.Total + g.Total,
                Unread = c.Unread + g.Unread,
                Starred = c.Starred + g.Starred,
                ListUnsubscribe = c.ListUnsubscribe + g.ListUnsubscribe,
                BulkHeader = c.BulkHeader + (bulk ? g.Total : 0),
                Primary = c.Primary + g.Primary,
                Promotions = c.Promotions + g.Promotions,
                Social = c.Social + g.Social,
                Updates = c.Updates + g.Updates,
                Forums = c.Forums + g.Forums,
                BulkOrListUnsubscribe = c.BulkOrListUnsubscribe + (bulk ? g.Total : g.ListUnsubscribe),
            };
        }

        var addresses = recent.Select(r => r.FromAddress).Distinct().ToArray();
        var allowlisted = Allowlist.CoversDomain(allowlistedDomains, domain)
            || await db.Senders.AnyAsync(s => addresses.Contains(s.Address) && s.Allowlisted, ct);
        var none = c.Total - c.Primary - c.Promotions - c.Social - c.Updates - c.Forums;
        return new SenderProfileStats(
            c.Total,
            Ratio(c.Unread, c.Total),
            c.Replied,
            c.Starred,
            Ratio(c.ListUnsubscribe, c.Total),
            Ratio(c.BulkHeader, c.Total),
            new CategoryMix(c.Primary, c.Promotions, c.Social, c.Updates, c.Forums, none),
            SenderStatsCalculator.Kind(c),
            groups.Count == 0 ? null : groups.Min(g => g.FirstSeen),
            groups.Count == 0 ? null : groups.Max(g => g.LastSeen),
            allowlisted);
    }

    /// <summary>Every template of <paramref name="recent"/> with its newest message, most messages first, then by template.</summary>
    private static List<(SenderTemplate Template, Recent Newest)> Templates(List<Recent> recent) =>
        recent
            .GroupBy(r => SubjectNormaliser.Template(r.Subject), StringComparer.Ordinal)
            .Select(g =>
            {
                var items = g.ToList();
                return (new SenderTemplate(
                    g.Key,
                    items.Count,
                    items[0].Subject,
                    2 * items.Count(r => !string.IsNullOrWhiteSpace(r.ListId)) >= items.Count,
                    2 * items.Count(r => !string.IsNullOrWhiteSpace(r.ListUnsubscribe)) >= items.Count,
                    CategoryMix.Of(items.Select(r => r.Category)),
                    Ratio(items.Count(r => r.HasAttachment), items.Count),
                    Ratio(items.Count(r => r.LabelIds.Contains(FilterSpec.Unread)), items.Count),
                    MostCommon(items.Select(r => r.Precedence).Where(p => !string.IsNullOrWhiteSpace(p)))), Newest: items[0]);
            })
            .OrderByDescending(t => t.Item1.Count)
            .ThenBy(t => t.Item1.Template, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The newest message of the top template, plus the newest of the first template whose dominant category differs
    /// (a mixed sender); bodies Gmail no longer returns or fails to return (other than not connected) are skipped.
    /// </summary>
    private async Task<IReadOnlyList<ProfileBody>> BodiesAsync(
        List<(SenderTemplate Template, Recent Newest)> shown, int maxChars, CancellationToken ct)
    {
        var top = shown[0].Template.CategoryMix.Dominant;
        var picks = shown.Take(1)
            .Concat(shown.Skip(1).Where(t => t.Template.CategoryMix.Dominant != top).Take(ProfileMaxBodies - 1))
            .ToList();

        var bodies = new List<ProfileBody>(picks.Count);
        foreach (var (template, message) in picks)
        {
            try
            {
                if (await gmail.GetMessageBodyAsync(message.Id, ct) is { } body)
                {
                    bodies.Add(new ProfileBody(template.Template, BodyCleaner.Clean(body.Text, body.Html, maxChars)));
                }
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or GmailNotConnectedException))
            {
                LogBodyUnavailable(logger, ex.GetType().Name);
            }
        }

        return bodies;
    }

    /// <summary>User labels by message count, then name; empty when Gmail cannot list labels (names come from Gmail).</summary>
    private async Task<IReadOnlyList<LabelUse>> LabelsInUseAsync(List<Recent> recent, CancellationToken ct)
    {
        IReadOnlyList<GmailLabel> all;
        try
        {
            all = await labels.GetAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogLabelsUnavailable(logger, ex.GetType().Name);
            return [];
        }

        var names = all.Where(l => l.Type == GmailLabelType.User).ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);
        return recent
            .SelectMany(r => r.LabelIds.Distinct())
            .Where(names.ContainsKey)
            .GroupBy(id => names[id], StringComparer.Ordinal)
            .Select(g => new LabelUse(g.Key, g.Count()))
            .OrderByDescending(l => l.Count)
            .ThenBy(l => l.Label, StringComparer.Ordinal)
            .Take(MaxLabels)
            .ToList();
    }

    /// <summary>Approved policies for the canonical domain or one of its senders, other than this profile's own.</summary>
    private async Task<IReadOnlyList<PolicyHint>> PolicyHintsAsync(PolicyScope scope, string scopeKey, string domain, CancellationToken ct)
    {
        if (domain.Length == 0)
        {
            return [];
        }

        var suffix = "@" + domain;
        return await db.SenderPolicies.AsNoTracking()
            .Where(p => p.Status == PolicyStatus.Approved
                && ((p.Scope == PolicyScope.Domain && p.ScopeKey == domain) || (p.Scope == PolicyScope.Sender && p.ScopeKey.EndsWith(suffix)))
                && !(p.Scope == scope && p.ScopeKey == scopeKey))
            .OrderBy(p => p.Scope).ThenBy(p => p.ScopeKey)
            .Take(MaxPolicyHints)
            .Select(p => new PolicyHint(p.Scope, p.ScopeKey, p.IsMixed, p.TopicLabel, p.DocumentTypeLabel, p.Action))
            .ToListAsync(ct);
    }

    /// <summary>The <paramref name="max"/> most frequent values, then ordinal.</summary>
    private static List<string> Ranked(IEnumerable<string> values, int max) =>
        values
            .GroupBy(v => v, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(max)
            .Select(g => g.Key)
            .ToList();

    private static string? MostCommon(IEnumerable<string?> values) => Ranked(values.OfType<string>(), 1).FirstOrDefault();

    private static double Ratio(int part, int total) => total == 0 ? 0 : Math.Round((double)part / total, 3);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sender profile built without labels in use: Gmail could not list labels ({Error})")]
    private static partial void LogLabelsUnavailable(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sender profile skipped a sample body: Gmail could not return it ({Error})")]
    private static partial void LogBodyUnavailable(ILogger logger, string error);

    private sealed record Recent(
        string Id,
        string FromAddress,
        string CanonicalDomain,
        string? FromName,
        string? Subject,
        string[] LabelIds,
        MessageCategory? Category,
        bool HasAttachment,
        string? ListId,
        string? ListUnsubscribe,
        string? Precedence);

    private sealed record HeaderGroup(
        string? Precedence,
        string? AutoSubmitted,
        string? ListId,
        int Total,
        int Unread,
        int Starred,
        int ListUnsubscribe,
        int Primary,
        int Promotions,
        int Social,
        int Updates,
        int Forums,
        DateTimeOffset FirstSeen,
        DateTimeOffset LastSeen);
}
