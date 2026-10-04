using System.Text.Json;
using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Llm;
using GmailOrganiser.Memory;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Analysis;

/// <summary>
/// What one group produced: rows to store, members that failed, members to analyse one by one, and the attachments of
/// the prompt's emails converted or skipped.
/// </summary>
internal sealed record GroupOutcome(
    IReadOnlyList<SuggestionRow> Suggestions,
    IReadOnlyList<string> FailedIds,
    IReadOnlyList<MessageGroup> Individual,
    int LlmCalls,
    bool Mixed,
    int AttachmentsConverted = 0,
    int AttachmentsSkipped = 0);

/// <summary>A representative's body and its attachment list (empty with the master switch off); both live only in the group's analysis.</summary>
internal sealed record FetchedMessage(GmailMessageBody Body, IReadOnlyList<GmailAttachment> Attachments);

/// <summary>A group's looked-ahead memory: its short-circuit result, or the vectors of its representatives.</summary>
internal sealed record PreparedGroup(ShortCircuitResult? Covered, MessageVectors? Vectors);

public sealed partial class AnalysisRunJob
{
    public const string RetryInstruction = "Return only the JSON array.";

    /// <summary>Representative bodies fetched at once; the Gmail quota limiter still paces the calls.</summary>
    public const int MaxConcurrentBodyFetches = 4;

    /// <summary>Groups looked ahead at once: one memory lookup and one embedding call for their representatives.</summary>
    public const int MemoryLookaheadGroups = 32;

    /// <summary>
    /// Looks ahead over the next groups not yet prepared (at most <see cref="MemoryLookaheadGroups"/>): the memory
    /// short-circuit for all of them in one lookup, then the vectors of the model-bound representatives in one call.
    /// </summary>
    private async Task PrepareAsync(
        RunContext context, IEnumerable<MessageGroup> upcoming, Dictionary<MessageGroup, PreparedGroup> prepared, CancellationToken ct)
    {
        var batch = upcoming.Where(g => !prepared.ContainsKey(g)).Take(MemoryLookaheadGroups).ToList();
        var covered = await shortCircuit.TryAsync(batch, new ShortCircuitContext(context.Settings, context.Allowlisted, context.LabelIndex, context.Labels), ct);
        var representatives = batch.Where((_, i) => covered[i] is null).SelectMany(Representatives).ToList();
        var vectors = representatives.Count == 0 ? null : await memory.EmbedMessagesAsync(representatives, ct);
        for (var i = 0; i < batch.Count; i++)
        {
            prepared[batch[i]] = new PreparedGroup(covered[i], vectors);
        }
    }

    private static IEnumerable<MessageRow> Representatives(MessageGroup group) =>
        group.RepresentativeIds.Select(id => group.Members.First(m => m.Id == id));

    /// <summary>
    /// Short-circuit, else one model call about the representatives (plus one retry when some answers are invalid).
    /// Nothing is written here. Bodies and attachment text live only in this call's locals.
    /// </summary>
    private async Task<GroupOutcome> AnalyseGroupAsync(RunContext context, MessageGroup group, PreparedGroup ready, CancellationToken ct)
    {
        if (ready.Covered is { } covered)
        {
            return FromMemory(context, group, covered);
        }

        var members = group.Members.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var representatives = group.RepresentativeIds.Select(id => members[id]).ToList();
        var fetched = new FetchedMessage?[representatives.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, representatives.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentBodyFetches, CancellationToken = ct },
            async (i, c) => fetched[i] = await FetchAsync(context, representatives[i], c));
        var present = representatives.Zip(fetched).Where(x => x.Second is not null).ToList();
        var emails = present
            .Select(x => new EmailForPrompt(
                x.First.Id, x.First.FromAddress, x.First.FromName, x.First.Subject, x.First.InternalDate, x.First.Category?.ToString(),
                !string.IsNullOrEmpty(x.First.ListUnsubscribe), x.First.HasAttachment,
                BodyCleaner.Clean(x.Second!.Body.Text, x.Second.Body.Html, context.Settings.AnalysisBodyMaxChars),
                context.Labels.NamesOf(x.First)))
            .ToList();

        // One message's attachments at a time, and groups run one after another: conversions (OCR, vision, office
        // documents) never compete with each other or with the analysis model.
        var converted = new List<IReadOnlyList<PromptAttachment>>();
        foreach (var (message, content) in present)
        {
            converted.Add(await attachments.ConvertAsync(message.Id, content!.Attachments, context.Attachments, ct));
        }

        var attachmentsSection = attachments.Render([.. present.Select((x, i) => new MessageAttachments(i + 1, x.First.Id, converted[i]))]);

        if (emails.Count < representatives.Count)
        {
            LogMissingBodies(logger, representatives.Count - emails.Count);
        }

        var (outputs, filter, calls) = emails.Count == 0
            ? ([], null, 0)
            : await AskModelAsync(
                context,
                emails,
                await memory.FindSimilarAsync(representatives, ready.Vectors, DecisionMemory.DefaultSimilarCount, ct),
                attachmentsSection,
                ct);
        if (outputs.Count == 0)
        {
            // Nothing usable: the members stay not analysed rather than costing one call each against a failing model.
            return new GroupOutcome([], [.. group.Members.Select(m => m.Id)], [], calls, Mixed: false);
        }

        outputs = WithoutAttachmentText(outputs, converted);

        // Counted for the analysed representatives only, so the totals match the messages that got a suggestion.
        var counted = present.Select((x, i) => outputs.ContainsKey(x.First.Id) ? converted[i] : []).SelectMany(l => l).ToList();
        var (convertedCount, skipped) = (counted.Count(a => a.Converted is not null), counted.Count(a => a.Skipped is not null));

        var filterJson = filter is null ? null : JsonSerializer.Serialize(filter, JsonSerializerOptions.Web);
        var groupKey = group.Individual ? null : group.Key;
        var rows = outputs.Values.Select(o => Row(context, members[o.Id], SuggestionSource.Llm, o, groupKey, filterJson)).ToList();
        var failed = representatives.Where(m => !outputs.ContainsKey(m.Id)).Select(m => m.Id).ToList();
        var others = group.Members.Where(m => !group.RepresentativeIds.Contains(m.Id, StringComparer.Ordinal)).ToList();
        if (others.Count == 0)
        {
            return new GroupOutcome(rows, failed, [], calls, Mixed: false, convertedCount, skipped);
        }

        var decision = DerivationRule.Decide(
            [.. representatives.Select(m => outputs.TryGetValue(m.Id, out var o)
                ? new RepresentativeOutput(o.TopicLabel, o.NeedsAction, o.ToBeDeleted, o.UnsubscribeSuggested, o.Confidence, o.ReplaceLabels, o.DocumentTypeLabel)
                : null)],
            context.Settings.AnalysisDerivedConfidencePenalty);
        if (decision is not Agreed agreed)
        {
            return new GroupOutcome(rows, failed, [.. others.Select(AnalysisGrouper.Single)], calls, Mixed: true, convertedCount, skipped);
        }

        // A derived toBeDeleted never lands on protected mail; the grouper makes such members representatives, this
        // only guards the rule if a refiner ever does not.
        var individual = new List<MessageGroup>();
        var isNewLabel = outputs.Values.Any(o => o.IsNewLabel && string.Equals(o.TopicLabel.Trim(), agreed.TopicLabel, StringComparison.OrdinalIgnoreCase));
        var derived = new SuggestionOutput(
            "", agreed.TopicLabel, isNewLabel, agreed.NeedsAction, agreed.ToBeDeleted, agreed.UnsubscribeSuggested, agreed.Confidence,
            $"Same as {outputs.Count} analysed emails of this group", agreed.DocumentTypeLabel)
        {
            ReplaceLabels = agreed.ReplaceLabels,
        };
        foreach (var m in others)
        {
            if (agreed.ToBeDeleted && MessageProtection.IsProtected(m, context.Allowlisted, context.Settings.Protection))
            {
                individual.Add(AnalysisGrouper.Single(m));
            }
            else
            {
                rows.Add(Row(context, m, SuggestionSource.Derived, derived, groupKey, filterJson));
            }
        }

        return new GroupOutcome(rows, failed, individual, calls, Mixed: false, convertedCount, skipped);
    }

    /// <summary>
    /// The body, plus the attachment list when the master switch is on: both from the one <c>messages.get</c> a body
    /// costs, whatever <see cref="MessageRow.HasAttachment"/> says (it misses signed and nested multiparts). Null when
    /// Gmail no longer knows the message.
    /// </summary>
    private async Task<FetchedMessage?> FetchAsync(RunContext context, MessageRow message, CancellationToken ct)
    {
        if (!context.Attachments.Enabled)
        {
            return await gmail.GetMessageBodyAsync(message.Id, ct) is { } body ? new FetchedMessage(body, []) : null;
        }

        return await gmail.GetMessageContentAsync(message.Id, ct) is { } content ? new FetchedMessage(content.Body, content.Attachments) : null;
    }

    /// <summary>The outputs, a reason that quotes an attachment of the prompt replaced by one naming it (#74).</summary>
    private static Dictionary<string, SuggestionOutput> WithoutAttachmentText(
        Dictionary<string, SuggestionOutput> outputs, IEnumerable<IReadOnlyList<PromptAttachment>> converted)
    {
        var texts = converted.SelectMany(l => l).Select(a => a.Converted).OfType<ConvertedAttachment>().ToList();
        return texts.Count == 0
            ? outputs
            : outputs.ToDictionary(
                kv => kv.Key, kv => kv.Value with { Reason = AttachmentReasonGuard.Scrub(kv.Value.Reason, texts) }, StringComparer.Ordinal);
    }

    /// <summary>
    /// The prompt, then the same prompt with <see cref="RetryInstruction"/> when an email has no valid answer. Failed
    /// ids are the expected ids without a valid answer, never derived from the error count.
    /// </summary>
    private async Task<(Dictionary<string, SuggestionOutput> Outputs, FilterCriteriaOutput? Filter, int Calls)> AskModelAsync(
        RunContext context, IReadOnlyList<EmailForPrompt> emails, IReadOnlyList<MemoryHint> hints, string attachmentsSection, CancellationToken ct)
    {
        var expected = emails.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var messages = context.Builder.Build(new PromptInput(
            emails, context.LabelTree, hints, attachmentsSection, context.Settings.ActionLabelName, context.Settings.DeleteLabelName));

        var current = emails.ToDictionary(e => e.Id, e => e.Labels, StringComparer.Ordinal);
        var parent = context.Run.DocumentTypeParent;
        var first = SuggestionOutputParser.Parse(await ChatAsync(context, messages, ct), expected, current, parent);
        LogDropped(first);
        var outputs = first.Valid.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var filter = first.Filter;
        if (outputs.Count == expected.Count)
        {
            return (outputs, filter, 1);
        }

        var retry = SuggestionOutputParser.Parse(
            await ChatAsync(context, [.. messages, new ChatMessage(ChatRole.User, RetryInstruction)], ct), expected, current, parent);
        LogDropped(retry);
        foreach (var o in retry.Valid)
        {
            outputs.TryAdd(o.Id, o);
        }

        filter ??= retry.Filter;
        if (outputs.Count < expected.Count)
        {
            // Parser errors name ids and fields only, never email content.
            LogInvalidOutput(logger, expected.Count - outputs.Count, string.Join("; ", retry.Errors));
        }

        return (outputs, filter, 2);
    }

    private void LogDropped(ParsedSuggestions parsed)
    {
        if (parsed.Dropped.Count > 0)
        {
            LogDroppedOutput(logger, string.Join("; ", parsed.Dropped));
        }
    }

    private async Task<string> ChatAsync(RunContext context, IList<ChatMessage> messages, CancellationToken ct)
    {
        try
        {
            return (await context.Chat.GetResponseAsync(messages, AnalysisPromptBuilder.CreateOptions(), ct)).Text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OllamaSharp.Models.Exceptions.OllamaException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new AnalysisModelUnavailableException(
                OllamaErrors.Describe(ex, OllamaHttp.Parse(context.Settings.OllamaBaseUrl), llmOptions.Value.ModelTimeout), ex);
        }
    }

    /// <summary>Stores the covered members as memory suggestions; the rest (protected mail) go to the model one by one.</summary>
    private GroupOutcome FromMemory(RunContext context, MessageGroup group, ShortCircuitResult covered)
    {
        var members = group.Members.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var ids = covered.Suggestions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0 || ids.Count != covered.Suggestions.Count || !ids.IsSubsetOf(members.Keys))
        {
            throw new InvalidOperationException("A short-circuit result must cover members of its group at most once each.");
        }

        var groupKey = group.Individual ? null : group.Key;
        return new GroupOutcome(
            [.. covered.Suggestions.Select(s => Row(context, members[s.Id], SuggestionSource.Memory, s, groupKey, null))],
            [],
            [.. group.Members.Where(m => !ids.Contains(m.Id)).Select(AnalysisGrouper.Single)],
            0,
            Mixed: false);
    }

    /// <summary>
    /// The replaced labels resolve to the ids this message carries: a derived member without one loses nothing. The
    /// document-type label takes the spelling of the run's label tree entry it matches and is new when there is none, so
    /// a derived row agrees with its representatives.
    /// </summary>
    private static SuggestionRow Row(
        RunContext context, MessageRow message, SuggestionSource source, SuggestionOutput output, string? groupKey, string? filterJson)
    {
        var existingType = output.DocumentTypeLabel is { } type ? context.LabelIndex.Find(type) : null;
        var row = new SuggestionRow
        {
            Id = Guid.CreateVersion7(),
            MessageId = message.Id,
            SenderAddress = message.FromAddress,
            GroupKey = groupKey,
            Source = source,
            TopicLabel = output.TopicLabel,
            IsNewLabel = output.IsNewLabel,
            DocumentTypeLabel = existingType ?? output.DocumentTypeLabel,
            DocumentTypeIsNew = output.DocumentTypeLabel is not null && existingType is null,
            NeedsAction = output.NeedsAction,
            ToBeDeleted = output.ToBeDeleted,
            UnsubscribeSuggested = output.UnsubscribeSuggested,
            // The DB check rejects NaN and out-of-range values; the parser and DerivationRule already clamp, this keeps it so.
            Confidence = double.IsFinite(output.Confidence) ? Math.Clamp(output.Confidence, 0, 1) : 0,
            Reason = output.Reason,
            FilterCriteria = filterJson,
            Model = context.Run.Model,
            PromptVersion = context.Run.PromptVersion,
        };
        row.SetReplaced(context.Labels.Carried(message, output.ReplaceLabels, output.TopicLabel));
        return row;
    }
}
