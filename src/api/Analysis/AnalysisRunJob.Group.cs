using System.Text.Json;
using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Llm;
using GmailOrganiser.Memory;
using GmailOrganiser.Settings;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Analysis;

/// <summary>
/// What one group produced: rows to store, members that failed, members to analyse one by one, the attachments of
/// the prompt's emails converted or skipped, and the tokens and time its model calls spent. <see cref="LlmCalls"/>
/// counts every call, <see cref="TriageCalls"/> the triage model's and <see cref="EscalatedCalls"/> those repeated
/// with the chat model.
/// </summary>
internal sealed record GroupOutcome(
    IReadOnlyList<SuggestionRow> Suggestions,
    IReadOnlyList<string> FailedIds,
    IReadOnlyList<MessageGroup> Individual,
    int LlmCalls,
    bool Mixed,
    int AttachmentsConverted = 0,
    int AttachmentsSkipped = 0,
    LlmUsage Usage = default,
    int TriageCalls = 0,
    int EscalatedCalls = 0);

/// <summary>
/// The valid answers about a group's representatives, the ids the triage model answered (the chat model answered the
/// rest), the filter proposal and what the calls cost.
/// </summary>
internal sealed record ModelAnswer(
    IReadOnlyDictionary<string, SuggestionOutput> Outputs,
    IReadOnlySet<string> TriageIds,
    FilterCriteriaOutput? Filter,
    int Calls,
    LlmUsage Usage,
    int TriageCalls = 0,
    int EscalatedCalls = 0)
{
    public static ModelAnswer None => new(new Dictionary<string, SuggestionOutput>(), new HashSet<string>(), null, 0, default);
}

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
        // A compare run shows what the current prompt does, so memory never answers for the model.
        var covered = context.Run.Kind == AnalysisRunKind.Compare
            ? new ShortCircuitResult?[batch.Count]
            : await shortCircuit.TryAsync(batch, new ShortCircuitContext(context.Settings, context.Allowlisted, context.LabelIndex, context.Labels, context.Run.DocumentTypeParent), ct);
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

        var answer = emails.Count == 0
            ? ModelAnswer.None
            : await AskModelAsync(
                context,
                emails,
                await memory.FindSimilarAsync(
                    representatives, ready.Vectors, DecisionMemory.DefaultSimilarCount, context.Run.DocumentTypeParent,
                    context.HintExclusions, ct),
                attachmentsSection,
                ct);
        if (answer.Outputs.Count == 0)
        {
            // Nothing usable: the members stay not analysed rather than costing one call each against a failing model.
            return new GroupOutcome(
                [], [.. group.Members.Select(m => m.Id)], [], answer.Calls, Mixed: false, Usage: answer.Usage,
                TriageCalls: answer.TriageCalls, EscalatedCalls: answer.EscalatedCalls);
        }

        var (calls, usage, triageCalls, escalatedCalls) = (answer.Calls, answer.Usage, answer.TriageCalls, answer.EscalatedCalls);
        var triageModel = context.Settings.TriageModel;
        // Derived rows record the chat model when it answered for the group, else the triage model.
        var groupModel = answer.Outputs.Count > answer.TriageIds.Count ? context.Run.Model : triageModel;

        var outputs = WithoutAttachmentText(answer.Outputs, converted);

        // Counted for the analysed representatives only, so the totals match the messages that got a suggestion.
        var counted = present.Select((x, i) => outputs.ContainsKey(x.First.Id) ? converted[i] : []).SelectMany(l => l).ToList();
        var (convertedCount, skipped) = (counted.Count(a => a.Converted is not null), counted.Count(a => a.Skipped is not null));

        var filterJson = answer.Filter is null ? null : JsonSerializer.Serialize(answer.Filter, JsonSerializerOptions.Web);
        var groupKey = group.Individual ? null : group.Key;
        var rows = outputs.Values
            .Select(o => Row(context, members[o.Id], SuggestionSource.Llm, o, groupKey, filterJson, answer.TriageIds.Contains(o.Id) ? triageModel : context.Run.Model))
            .ToList();
        var failed = representatives.Where(m => !outputs.ContainsKey(m.Id)).Select(m => m.Id).ToList();
        var others = group.Members.Where(m => !group.RepresentativeIds.Contains(m.Id, StringComparer.Ordinal)).ToList();
        if (others.Count == 0)
        {
            return new GroupOutcome(rows, failed, [], calls, Mixed: false, convertedCount, skipped, usage, triageCalls, escalatedCalls);
        }

        var decision = DerivationRule.Decide(
            [.. representatives.Select(m => outputs.TryGetValue(m.Id, out var o)
                ? new RepresentativeOutput(o.TopicLabel, o.NeedsAction, o.ToBeDeleted, o.UnsubscribeSuggested, o.Confidence, o.ReplaceLabels, o.DocumentTypeLabel)
                : null)],
            context.Settings.AnalysisDerivedConfidencePenalty);
        if (decision is not Agreed agreed)
        {
            return new GroupOutcome(
                rows, failed, [.. others.Select(AnalysisGrouper.Single)], calls, Mixed: true, convertedCount, skipped, usage, triageCalls, escalatedCalls);
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
                rows.Add(Row(context, m, SuggestionSource.Derived, derived, groupKey, filterJson, groupModel));
            }
        }

        return new GroupOutcome(rows, failed, individual, calls, Mixed: false, convertedCount, skipped, usage, triageCalls, escalatedCalls);
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
    private static IReadOnlyDictionary<string, SuggestionOutput> WithoutAttachmentText(
        IReadOnlyDictionary<string, SuggestionOutput> outputs, IEnumerable<IReadOnlyList<PromptAttachment>> converted)
    {
        var texts = converted.SelectMany(l => l).Select(a => a.Converted).OfType<ConvertedAttachment>().ToList();
        return texts.Count == 0
            ? outputs
            : outputs.ToDictionary(
                kv => kv.Key, kv => kv.Value with { Reason = AttachmentReasonGuard.Scrub(kv.Value.Reason, texts) }, StringComparer.Ordinal);
    }

    /// <summary>
    /// With a triage model (#375): its answer when every email has a valid answer at or above the threshold (errors that
    /// name no email, such as an unusable filter or an unknown id, do not count). Otherwise the chat model answers the same
    /// prompt, retrying only for emails neither model answered confidently; the chat answer wins per email, a triage answer
    /// (confident or not) fills the emails the chat model left without one, and the triage filter stands in for a missing
    /// chat filter. Without a triage model: the chat model's answer only.
    /// </summary>
    private async Task<ModelAnswer> AskModelAsync(
        RunContext context, IReadOnlyList<EmailForPrompt> emails, IReadOnlyList<MemoryHint> hints, string attachmentsSection, CancellationToken ct)
    {
        var expected = emails.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var messages = context.Builder.Build(new PromptInput(
            emails, context.LabelTree, hints, attachmentsSection, context.Settings.ActionLabelName, context.Settings.DeleteLabelName,
            context.Run.DocumentTypeParent));
        var current = emails.ToDictionary(e => e.Id, e => e.Labels, StringComparer.Ordinal);
        if (context.Triage is not { } triage)
        {
            return await AskChatModelAsync(context, messages, expected, new HashSet<string>(), current, ct);
        }

        string text;
        LlmUsage usage;
        try
        {
            (text, usage) = await ChatAsync(context, triage, context.Settings.TriageModel, messages, emails.Count, ct);
        }
        catch (AnalysisModelUnavailableException ex)
        {
            // Triage is an optimisation: an unreachable triage model hands the group to the chat model, whose own
            // unavailability still fails the run.
            LogTriageUnavailable(logger, ex.Message);
            var escalated = await AskChatModelAsync(context, messages, expected, new HashSet<string>(), current, ct);
            return escalated with { Calls = escalated.Calls + 1, TriageCalls = 1, EscalatedCalls = 1 };
        }

        var parsed = SuggestionOutputParser.Parse(text, expected, current, context.Run.DocumentTypeParent);
        LogDropped(parsed);
        var threshold = context.Settings.TriageConfidenceThreshold;
        var triaged = parsed.Valid.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var confident = triaged.Values.Where(o => o.Confidence >= threshold).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        if (confident.Count == expected.Count)
        {
            return new ModelAnswer(triaged, confident, parsed.Filter, 1, usage, TriageCalls: 1);
        }

        // Parser errors name ids and fields only, never email content.
        LogTriageEscalated(
            logger, expected.Count - triaged.Count, triaged.Count - confident.Count, threshold, string.Join("; ", parsed.Errors));
        var chat = await AskChatModelAsync(context, messages, expected, confident, current, ct);
        var outputs = new Dictionary<string, SuggestionOutput>(chat.Outputs, StringComparer.Ordinal);
        var filled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, output) in triaged)
        {
            if (outputs.TryAdd(id, output))
            {
                filled.Add(id);
            }
        }

        return new ModelAnswer(
            outputs, filled, chat.Filter ?? parsed.Filter, chat.Calls + 1, usage + chat.Usage, TriageCalls: 1, EscalatedCalls: 1);
    }

    /// <summary>
    /// The prompt, then the same prompt with <see cref="RetryInstruction"/> when an email has neither a valid answer nor
    /// one in <paramref name="answered"/> (the triage model's confident answers). Failed ids are the expected ids without
    /// a valid answer, never derived from the error count.
    /// </summary>
    private async Task<ModelAnswer> AskChatModelAsync(
        RunContext context, IList<ChatMessage> messages, HashSet<string> expected, IReadOnlySet<string> answered,
        IReadOnlyDictionary<string, IReadOnlyList<string>> current, CancellationToken ct)
    {
        var parent = context.Run.DocumentTypeParent;
        var model = context.Run.Model;
        var (firstText, usage) = await ChatAsync(context, context.Chat, model, messages, expected.Count, ct);
        var first = SuggestionOutputParser.Parse(firstText, expected, current, parent);
        LogDropped(first);
        var outputs = first.Valid.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var filter = first.Filter;
        if (Unanswered() == 0)
        {
            return Answer(1);
        }

        var (retryText, retryUsage) = await ChatAsync(
            context, context.Chat, model, [.. messages, new ChatMessage(ChatRole.User, RetryInstruction)], expected.Count, ct);
        usage += retryUsage;
        var retry = SuggestionOutputParser.Parse(retryText, expected, current, parent);
        LogDropped(retry);
        foreach (var o in retry.Valid)
        {
            outputs.TryAdd(o.Id, o);
        }

        filter ??= retry.Filter;
        if (Unanswered() is > 0 and var unanswered)
        {
            // Parser errors name ids and fields only, never email content.
            LogInvalidOutput(logger, unanswered, string.Join("; ", retry.Errors));
        }

        return Answer(2);

        int Unanswered() => expected.Count(id => !outputs.ContainsKey(id) && !answered.Contains(id));

        ModelAnswer Answer(int calls) => new(outputs, new HashSet<string>(), filter, calls, usage);
    }

    private void LogDropped(ParsedSuggestions parsed)
    {
        if (parsed.Dropped.Count > 0)
        {
            LogDroppedOutput(logger, string.Join("; ", parsed.Dropped));
        }
    }

    private Task<(string Text, LlmUsage Usage)> ChatAsync(
        RunContext context, IChatClient chat, string? model, IList<ChatMessage> messages, int groupSize, CancellationToken ct) =>
        ChatAsync(context.Settings, chat, model, messages, AnalysisPromptBuilder.CreateOptions(context.Settings.LlmNumCtx), groupSize, ct);

    /// <summary>One metered chat call; an unreachable or timed-out model becomes <see cref="AnalysisModelUnavailableException"/>.</summary>
    private async Task<(string Text, LlmUsage Usage)> ChatAsync(
        AppSettings settings, IChatClient chat, string? model, IList<ChatMessage> messages, ChatOptions options, int groupSize,
        CancellationToken ct)
    {
        try
        {
            var (response, usage) = await _meter.GetResponseAsync(chat, messages, options, model, settings.LlmNumCtx, groupSize, ct);
            return (response.Text, usage);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OllamaSharp.Models.Exceptions.OllamaException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new AnalysisModelUnavailableException(
                OllamaErrors.Describe(ex, OllamaHttp.Parse(settings.OllamaBaseUrl), llmOptions.Value.ModelTimeout), ex);
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
            [.. covered.Suggestions.Select(s => Row(context, members[s.Id], SuggestionSource.Memory, s, groupKey, null, context.Run.Model))],
            [],
            [.. group.Members.Where(m => !ids.Contains(m.Id)).Select(AnalysisGrouper.Single)],
            0,
            Mixed: false);
    }

    /// <summary>
    /// The replaced labels resolve to the ids this message carries: a derived member without one loses nothing. The
    /// document-type label takes the spelling of the run's label tree entry it matches (for a new one, of its longest
    /// existing ancestor) and is new when there is none, so a derived row agrees with its representatives.
    /// </summary>
    private static SuggestionRow Row(
        RunContext context, MessageRow message, SuggestionSource source, SuggestionOutput output, string? groupKey, string? filterJson,
        string? model)
    {
        var type = output.DocumentTypeLabel is { } suggested ? context.LabelIndex.Respell(suggested) : null;
        var row = new SuggestionRow
        {
            Id = Guid.CreateVersion7(),
            MessageId = message.Id,
            SenderAddress = message.FromAddress,
            GroupKey = groupKey,
            Source = source,
            TopicLabel = output.TopicLabel,
            IsNewLabel = output.IsNewLabel,
            DocumentTypeLabel = type,
            DocumentTypeIsNew = type is not null && !context.LabelIndex.Contains(type),
            NeedsAction = output.NeedsAction,
            ToBeDeleted = output.ToBeDeleted,
            UnsubscribeSuggested = output.UnsubscribeSuggested,
            // The DB check rejects NaN and out-of-range values; the parser and DerivationRule already clamp, this keeps it so.
            Confidence = double.IsFinite(output.Confidence) ? Math.Clamp(output.Confidence, 0, 1) : 0,
            Reason = output.Reason,
            FilterCriteria = filterJson,
            Model = model,
            PromptVersion = context.Run.PromptVersion,
        };
        row.SetReplaced(context.Labels.Carried(message, output.ReplaceLabels, output.TopicLabel));
        return row;
    }
}
