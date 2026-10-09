using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Llm;
using GmailOrganiser.Policies;
using GmailOrganiser.Review;
using GmailOrganiser.Rules.Labels;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Rules.Taxonomy;

/// <summary>
/// Proposes the area-label taxonomy once (#366, DESIGN §6.5): profiles the top <see cref="AppSettings.TaxonomyMaxSenders"/>
/// canonical senders by volume (no bodies; allowlisted and human senders left out), asks the chat model once with
/// <see cref="TaxonomyPrompt"/> and stores the answer as the draft label plan, its notes as the plan's warnings. Nothing
/// is written before the final transaction, so a resume reruns the read-only steps; an answer that is not valid fails
/// the job with the reason.
/// </summary>
public sealed partial class TaxonomyProposeJob(
    AppDbContext db,
    SenderProfileBuilder profiles,
    LabelCatalog catalog,
    LabelPlanService plans,
    ILlmClientFactory llm,
    ISettingsStore settingsStore,
    TimeProvider time,
    ILogger<TaxonomyProposeJob> logger) : IJobHandler
{
    public const string JobType = "taxonomy_propose";
    public const string Queue = JobQueues.Analysis;

    /// <summary>Profiles built between two checkpoints, so a pause or cancel is honoured while profiling.</summary>
    public const int ProfilesPerCheckpoint = 10;

    private readonly LlmCallMeter _meter = new(time, logger);

    public string Type => JobType;

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        var model = settings.ActiveChatModel ?? throw new JobRefusedException(new LlmNotConfiguredException(ModelKinds.Chat).Message);
        var userLabels = (await catalog.RefreshAsync(ct)).Where(l => l.Type == GmailLabelType.User).ToList();
        var senders = await TopSendersAsync(settings, ct);

        // The last step is the model call.
        var steps = senders.Count + 1;
        var lines = new List<string>(senders.Count);
        var keys = new List<string>(senders.Count);
        for (var i = 0; i < senders.Count; i++)
        {
            if (i % ProfilesPerCheckpoint == 0
                && await ctx.CheckpointAsync<object?>(null, new JobProgress(i, steps, "Profiling senders"), ct) != JobSignal.Continue)
            {
                return;
            }

            if (await profiles.BuildAsync(PolicyScope.Sender, senders[i].Key, includeBodies: false, ct) is { } profile)
            {
                lines.Add(TaxonomyPrompt.ProfileLine(profile));
                keys.Add(senders[i].Key);
            }
        }

        if (await ctx.CheckpointAsync<object?>(null, new JobProgress(senders.Count, steps, "Asking the model"), ct) != JobSignal.Continue)
        {
            return;
        }

        var maxLabels = settings.TaxonomyMaxLabels;
        var messages = TaxonomyPrompt.Build(
            lines, [.. userLabels.Select(l => l.Name).Order(StringComparer.OrdinalIgnoreCase)], settings.DocumentTypeParent, maxLabels);
        string? answer;
        using (var chat = await llm.CreateChatClientAsync(ct))
        {
            var (response, _) = await _meter.GetResponseAsync(
                chat, messages, TaxonomyPrompt.CreateOptions(settings.LlmNumCtx), model, settings.MeterNumCtx, lines.Count, ct);
            answer = response.Text;
        }

        var protectedNames = LabelPlanService.ProtectedNames(settings);
        var parsed = TaxonomyPrompt.Parse(answer, keys, maxLabels, protectedNames, settings.DocumentTypeParent, out var error)
            ?? throw new JobRefusedException(error!);
        var items = TaxonomyPrompt.Items(parsed, userLabels, protectedNames, senders.ToDictionary(s => s.Key, s => s.Total, StringComparer.Ordinal));
        var now = time.GetUtcNow();
        var row = new LabelPlanRow
        {
            Id = Guid.CreateVersion7(now),
            Status = LabelPlanStatus.Draft,
            Warnings = [.. parsed.Notes],
            LabelCount = userLabels.Count,
            CreatedAt = now,
            UpdatedAt = now,
        };
        row.WriteItems(items);
        LogProposed(logger, items.Count, lines.Count, parsed.Notes.Count);
        await ctx.CompleteAsync<object?>(null, new JobProgress(steps, steps, $"Proposed {items.Count} labels"), t => plans.StoreDraftAsync(row, t), ct);
    }

    /// <summary>
    /// The canonical senders with the most stored messages, then by key: neither allowlisted (address or domain) nor
    /// human, since the taxonomy sorts bulk and automated mail.
    /// </summary>
    private async Task<List<(string Key, int Total)>> TopSendersAsync(AppSettings settings, CancellationToken ct)
    {
        // One row per canonical sender; the allowlisted domains are matched in memory (subdomains count).
        var allowlisted = db.Senders.Where(s => s.Allowlisted).Select(s => s.CanonicalAddress);
        var ranked = await db.Senders.AsNoTracking()
            .Where(s => s.CanonicalAddress != "" && s.Kind != SenderKind.Human && !allowlisted.Contains(s.CanonicalAddress))
            .GroupBy(s => s.CanonicalAddress)
            .Select(g => new { Key = g.Key, Domain = g.Max(s => s.CanonicalDomain)!, Total = g.Sum(s => s.TotalCount) })
            .Where(g => g.Total > 0)
            .ToListAsync(ct);
        return ranked
            .Where(s => !Allowlist.CoversDomain(settings.Protection.AllowlistedDomains, s.Domain))
            .OrderByDescending(s => s.Total)
            .ThenBy(s => s.Key, StringComparer.Ordinal)
            .Take(settings.TaxonomyMaxSenders)
            .Select(s => (s.Key, s.Total))
            .ToList();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Taxonomy proposal: {Labels} labels from {Senders} sender profiles, {Notes} notes")]
    private static partial void LogProposed(ILogger logger, int labels, int senders, int notes);
}
