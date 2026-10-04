using System.Globalization;
using System.Linq.Expressions;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Review;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Rules;

/// <summary>A validated filter request: the Gmail criteria and the action before its label names are resolved to ids.</summary>
public sealed record FilterSpec(GmailFilterCriteria Criteria, IReadOnlyList<string> AddLabelNames, bool SkipInbox, bool MarkRead)
{
    public const string Inbox = "INBOX";
    public const string Unread = "UNREAD";

    public IReadOnlyList<string> RemoveLabelIds => [.. new[] { SkipInbox ? Inbox : null, MarkRead ? Unread : null }.OfType<string>()];
}

/// <summary>
/// The one mapping of filter criteria: request validation (<see cref="TryRead"/>), the Gmail search query equivalent
/// (<see cref="ToQuery"/>) and the local count predicate (<see cref="LocalFilter"/>).
/// </summary>
public static class FilterCriteriaMapping
{
    /// <summary>The request as a <see cref="FilterSpec"/>, or null with <paramref name="errors"/> keyed by field.</summary>
    public static FilterSpec? TryRead(FilterCriteriaDto? criteria, FilterActionRequest? action, out Dictionary<string, string[]> errors)
    {
        errors = [];
        if (criteria is null)
        {
            errors["criteria"] = ["Required."];
        }

        if (action is null)
        {
            errors["action"] = ["Required."];
        }

        if (criteria is null || action is null)
        {
            return null;
        }

        var comparison = GmailSizeComparisons.Parse(criteria.SizeComparison);
        if (!string.IsNullOrWhiteSpace(criteria.SizeComparison) && comparison is null)
        {
            errors["criteria.sizeComparison"] = ["Must be 'larger' or 'smaller'."];
        }
        else if (criteria.Size is null != comparison is null || criteria.Size < 0)
        {
            errors["criteria.size"] = ["A size criterion needs a non-negative size and a comparison."];
        }

        var gmail = new GmailFilterCriteria(
            Clean(criteria.From), Clean(criteria.To), Clean(criteria.Subject), Clean(criteria.Query), Clean(criteria.NegatedQuery),
            criteria.HasAttachment == true ? true : null, criteria.ExcludeChats == true ? true : null, criteria.Size, comparison);
        if (!gmail.MatchesMail)
        {
            errors["criteria"] = ["At least one criterion besides excluding chats is required."];
        }

        var names = new List<string>();
        foreach (var raw in action.AddLabelNames ?? [])
        {
            var name = raw?.Trim() ?? "";
            if (!LabelResolver.IsValid(name))
            {
                errors["action.addLabelNames"] = [$"'{name}' is not a valid label path (or is a reserved Gmail label)."];
            }
            else if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        if (names.Count == 0 && !action.SkipInbox && !action.MarkRead)
        {
            errors["action"] = ["At least one action is required: a label, skip inbox or mark read."];
        }

        return errors.Count == 0 ? new FilterSpec(gmail, names, action.SkipInbox, action.MarkRead) : null;
    }

    /// <summary>
    /// The Gmail search query Gmail's "create filter from search" would show: <c>from:x</c> or <c>from:(a OR b)</c>,
    /// <c>to:</c>, <c>subject:</c>, <c>has:attachment</c>, <c>larger:/smaller:</c>, the query, and <c>-negated</c>;
    /// a multi-term value is parenthesised.
    /// </summary>
    public static string ToQuery(GmailFilterCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var parts = new List<string>();
        Add("from:", criteria.From);
        Add("to:", criteria.To);
        Add("subject:", criteria.Subject);
        if (criteria.HasAttachment == true)
        {
            parts.Add("has:attachment");
        }

        if (criteria is { Size: { } size, SizeComparison: { } comparison })
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{comparison.ToGmailString()}:{size}"));
        }

        Add("", criteria.Query);
        Add("-", criteria.NegatedQuery);
        return string.Join(' ', parts);

        void Add(string prefix, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                var v = value.Trim();
                parts.Add(prefix + (v.Any(char.IsWhiteSpace) ? $"({v})" : v));
            }
        }
    }

    /// <summary>
    /// The criteria as a filter on stored messages, or null with the <paramref name="reason"/> when a criterion has no
    /// local equivalent. <c>from</c>: an address, <c>@domain</c> (subdomains included) or an <c>OR</c> list of those;
    /// <c>to</c>/<c>subject</c>: case-insensitive contains; <c>query</c>: only <c>list:&lt;id&gt;</c>; <c>negatedQuery</c>:
    /// only <c>has:attachment</c> or <c>list:&lt;id&gt;</c>.
    /// </summary>
    public static Func<IQueryable<MessageRow>, IQueryable<MessageRow>>? LocalFilter(GmailFilterCriteria criteria, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        reason = null;
        var steps = new List<Expression<Func<MessageRow, bool>>>();
        if (!string.IsNullOrWhiteSpace(criteria.From))
        {
            if (FromPredicate(criteria.From) is not { } from)
            {
                reason = "'from' is not an address, @domain or an OR list of those";
                return null;
            }

            steps.Add(from);
        }

        if (!string.IsNullOrWhiteSpace(criteria.To))
        {
            var pattern = $"%{EscapeLike(criteria.To.Trim())}%";
            steps.Add(m => m.ToHeader != null && EF.Functions.ILike(m.ToHeader, pattern));
        }

        if (!string.IsNullOrWhiteSpace(criteria.Subject))
        {
            var pattern = $"%{EscapeLike(criteria.Subject.Trim())}%";
            steps.Add(m => m.Subject != null && EF.Functions.ILike(m.Subject, pattern));
        }

        if (criteria.HasAttachment == true)
        {
            steps.Add(m => m.HasAttachment);
        }

        if (criteria.Size is not null)
        {
            reason = "a size criterion is not counted locally";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(criteria.Query))
        {
            if (TermPredicate(criteria.Query, allowAttachment: false) is not { } query)
            {
                reason = "'query' is not of the form list:<id>";
                return null;
            }

            steps.Add(query);
        }

        if (!string.IsNullOrWhiteSpace(criteria.NegatedQuery))
        {
            if (TermPredicate(criteria.NegatedQuery, allowAttachment: true) is not { } negated)
            {
                reason = "'negatedQuery' is not has:attachment or list:<id>";
                return null;
            }

            steps.Add(Expression.Lambda<Func<MessageRow, bool>>(Expression.Not(negated.Body), negated.Parameters));
        }

        return messages => steps.Aggregate(messages, (q, step) => q.Where(step));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Expression<Func<MessageRow, bool>>? FromPredicate(string from)
    {
        var terms = from.Trim().Trim('(', ')').Split(" OR ", StringSplitOptions.TrimEntries);
        var m = Expression.Parameter(typeof(MessageRow), "m");
        var address = Expression.Property(m, nameof(MessageRow.FromAddress));
        Expression? body = null;
        foreach (var term in terms.Select(t => t.ToLowerInvariant()))
        {
            if (term.Length < 2 || term.Any(char.IsWhiteSpace) || term.Any(c => c is '(' or ')' or '"' or '*'))
            {
                return null;
            }

            Expression next;
            if (term[0] == '@')
            {
                if (term.IndexOf('@', 1) >= 0)
                {
                    return null;
                }

                next = Expression.OrElse(EndsWith(address, term), EndsWith(address, "." + term[1..]));
            }
            else if (term.Count(c => c == '@') == 1 && term[^1] != '@')
            {
                next = Expression.Equal(address, Expression.Constant(term));
            }
            else
            {
                return null;
            }

            body = body is null ? next : Expression.OrElse(body, next);
        }

        return body is null ? null : Expression.Lambda<Func<MessageRow, bool>>(body, m);
    }

    private static MethodCallExpression EndsWith(Expression value, string suffix) =>
        Expression.Call(value, typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!, Expression.Constant(suffix));

    /// <summary><c>list:&lt;id&gt;</c> (case-insensitive equality with the stored List-Id) or, when allowed, <c>has:attachment</c>.</summary>
    private static Expression<Func<MessageRow, bool>>? TermPredicate(string term, bool allowAttachment)
    {
        var t = term.Trim();
        if (allowAttachment && t.Equals("has:attachment", StringComparison.OrdinalIgnoreCase))
        {
            return m => m.HasAttachment;
        }

        if (t.StartsWith("list:", StringComparison.OrdinalIgnoreCase) && t.Length > 5 && !t.Any(char.IsWhiteSpace))
        {
            var id = EscapeLike(t[5..].Trim('<', '>'));
            return m => m.ListId != null && EF.Functions.ILike(m.ListId, id);
        }

        return null;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
