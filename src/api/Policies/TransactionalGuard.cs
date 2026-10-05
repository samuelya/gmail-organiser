using System.Text.RegularExpressions;
using GmailOrganiser.Fetch;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Policies;

/// <summary>
/// Whether a message looks transactional (an attachment, or a <see cref="PolicyOptions.TransactionalKeywords"/> word in
/// the subject or snippet); such mail is never deleted by a policy (<see cref="PolicyMatcher"/>). Bodies are not stored,
/// so only the subject and snippet are read. Pure: the keyword list is fixed at construction.
/// </summary>
public sealed class TransactionalGuard
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private readonly Regex? _keywords;

    public TransactionalGuard(IOptions<PolicyOptions> options)
    {
        var words = options.Value.TransactionalKeywords
            .Select(k => k.Trim())
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Regex.Escape)
            .ToList();
        // Whole words: no letter or digit on either side, so "due" does not hit "overdue" or "subdued".
        _keywords = words.Count == 0
            ? null
            : new Regex(
                $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join('|', words)})(?![\p{{L}}\p{{N}}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                MatchTimeout);
    }

    public bool IsTransactional(MessageRow m) =>
        m.HasAttachment || HasKeyword(m.Subject) || HasKeyword(m.Snippet);

    private bool HasKeyword(string? text)
    {
        if (_keywords is null || string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            return _keywords.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Err on the safe side: a message the guard cannot read is not deleted.
            return true;
        }
    }
}
