using System.Text.Json;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Llm;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Analysis;

/// <summary>What one group produced: rows to store, members that failed, members to analyse one by one.</summary>
internal sealed record GroupOutcome(
    IReadOnlyList<SuggestionRow> Suggestions,
    IReadOnlyList<string> FailedIds,
    IReadOnlyList<MessageGroup> Individual,
    int LlmCalls,
    bool Mixed);

public sealed partial class AnalysisRunJob
{
    public const string RetryInstruction = "Return only the JSON array.";

    /// <summary>Representative bodies fetched at once; the Gmail quota limiter still paces the calls.</summary>
    public const int MaxConcurrentBodyFetches = 4;

    /// <summary>
    /// Short-circuit, else one model call about the representatives (plus one retry when some answers are invalid).
    /// Nothing is written here. Bodies live only in this call's locals.
    /// </summary>
    private async Task<GroupOutcome> AnalyseGroupAsync(RunContext context, MessageGroup group, CancellationToken ct)
    {
        if (await shortCircuit.TryAsync(group, ct) is { } covered)
        {
            return FromMemory(context, group, covered);
        }

        var members = group.Members.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var representatives = group.RepresentativeIds.Select(id => members[id]).ToList();
        var bodies = new GmailMessageBody?[representatives.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, representatives.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentBodyFetches, CancellationToken = ct },
            async (i, c) => bodies[i] = await gmail.GetMessageBodyAsync(representatives[i].Id, c));
        var emails = representatives
            .Zip(bodies)
            .Where(x => x.Second is not null)
            .Select(x => new EmailForPrompt(
                x.First.Id, x.First.FromAddress, x.First.FromName, x.First.Subject, x.First.InternalDate, x.First.Category?.ToString(),
                !string.IsNullOrEmpty(x.First.ListUnsubscribe), x.First.HasAttachment,
                BodyCleaner.Clean(x.Second!.Text, x.Second.Html, context.Settings.AnalysisBodyMaxChars)))
            .ToList();

        if (emails.Count < representatives.Count)
        {
            LogMissingBodies(logger, representatives.Count - emails.Count);
        }

        var (outputs, filter, calls) = emails.Count == 0
            ? ([], null, 0)
            : await AskModelAsync(context, emails, ct);
        if (outputs.Count == 0)
        {
            // Nothing usable: the members stay not analysed rather than costing one call each against a failing model.
            return new GroupOutcome([], [.. group.Members.Select(m => m.Id)], [], calls, Mixed: false);
        }

        var filterJson = filter is null ? null : JsonSerializer.Serialize(filter, JsonSerializerOptions.Web);
        var groupKey = group.Individual ? null : group.Key;
        var rows = outputs.Values.Select(o => Row(context, members[o.Id], SuggestionSource.Llm, o, groupKey, filterJson)).ToList();
        var failed = representatives.Where(m => !outputs.ContainsKey(m.Id)).Select(m => m.Id).ToList();
        var others = group.Members.Where(m => !group.RepresentativeIds.Contains(m.Id, StringComparer.Ordinal)).ToList();
        if (others.Count == 0)
        {
            return new GroupOutcome(rows, failed, [], calls, Mixed: false);
        }

        var decision = DerivationRule.Decide(
            [.. representatives.Select(m => outputs.TryGetValue(m.Id, out var o)
                ? new RepresentativeOutput(o.TopicLabel, o.NeedsAction, o.ToBeDeleted, o.UnsubscribeSuggested, o.Confidence)
                : null)],
            context.Settings.AnalysisDerivedConfidencePenalty);
        if (decision is not Agreed agreed)
        {
            return new GroupOutcome(rows, failed, [.. others.Select(AnalysisGrouper.Single)], calls, Mixed: true);
        }

        // A derived toBeDeleted never lands on protected mail; the grouper makes such members representatives, this
        // only guards the rule if a refiner ever does not.
        var individual = new List<MessageGroup>();
        var isNewLabel = outputs.Values.Any(o => o.IsNewLabel && string.Equals(o.TopicLabel.Trim(), agreed.TopicLabel, StringComparison.OrdinalIgnoreCase));
        var derived = new SuggestionOutput(
            "", agreed.TopicLabel, isNewLabel, agreed.NeedsAction, agreed.ToBeDeleted, agreed.UnsubscribeSuggested, agreed.Confidence,
            $"Same as {outputs.Count} analysed emails of this group");
        foreach (var m in others)
        {
            if (agreed.ToBeDeleted && MessageProtection.IsProtected(m, context.Allowlisted))
            {
                individual.Add(AnalysisGrouper.Single(m));
            }
            else
            {
                rows.Add(Row(context, m, SuggestionSource.Derived, derived, groupKey, filterJson));
            }
        }

        return new GroupOutcome(rows, failed, individual, calls, Mixed: false);
    }

    /// <summary>
    /// The prompt, then the same prompt with <see cref="RetryInstruction"/> when an email has no valid answer. Failed
    /// ids are the expected ids without a valid answer, never derived from the error count.
    /// </summary>
    private async Task<(Dictionary<string, SuggestionOutput> Outputs, FilterCriteriaOutput? Filter, int Calls)> AskModelAsync(
        RunContext context, IReadOnlyList<EmailForPrompt> emails, CancellationToken ct)
    {
        var expected = emails.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var messages = context.Builder.Build(new PromptInput(
            emails, context.LabelTree, [], null, context.Settings.ActionLabelName, context.Settings.DeleteLabelName));

        var first = SuggestionOutputParser.Parse(await ChatAsync(context, messages, ct), expected);
        var outputs = first.Valid.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var filter = first.Filter;
        if (outputs.Count == expected.Count)
        {
            return (outputs, filter, 1);
        }

        var retry = SuggestionOutputParser.Parse(
            await ChatAsync(context, [.. messages, new ChatMessage(ChatRole.User, RetryInstruction)], ct), expected);
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

    private GroupOutcome FromMemory(RunContext context, MessageGroup group, ShortCircuitResult covered)
    {
        var ids = covered.Suggestions.Select(s => s.Id).ToList();
        if (ids.Count != group.Members.Count || !ids.ToHashSet(StringComparer.Ordinal).SetEquals(group.Members.Select(m => m.Id)))
        {
            throw new InvalidOperationException("A short-circuit result must cover every member of its group exactly once.");
        }

        var members = group.Members.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var groupKey = group.Individual ? null : group.Key;
        return new GroupOutcome(
            [.. covered.Suggestions.Select(s => Row(context, members[s.Id], SuggestionSource.Memory, s, groupKey, null))], [], [], 0, Mixed: false);
    }

    private static SuggestionRow Row(
        RunContext context, MessageRow message, SuggestionSource source, SuggestionOutput output, string? groupKey, string? filterJson) => new()
        {
            Id = Guid.CreateVersion7(),
            MessageId = message.Id,
            SenderAddress = message.FromAddress,
            GroupKey = groupKey,
            Source = source,
            TopicLabel = output.TopicLabel,
            IsNewLabel = output.IsNewLabel,
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
}
