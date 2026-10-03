using System.Globalization;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// Keeps attachment text out of stored reasons (#74): a reason that shares a run of <see cref="RunWords"/> consecutive
/// words with an attachment of its prompt is replaced by one that names the attachment only. Words compare
/// case-insensitively, whitespace and the punctuation around a word ignored.
/// </summary>
public static class AttachmentReasonGuard
{
    public const int RunWords = 8;

    public static string Neutral(string filename) =>
        string.Create(CultureInfo.InvariantCulture, $"Based on the attachment {string.Join(' ', filename.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}.");

    /// <summary><paramref name="reason"/>, or <see cref="Neutral"/> for the first attachment it quotes.</summary>
    public static string Scrub(string reason, IReadOnlyList<ConvertedAttachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(attachments);
        var runs = Runs(reason).ToHashSet(StringComparer.Ordinal);
        if (runs.Count == 0)
        {
            return reason;
        }

        var quoted = attachments.FirstOrDefault(a => Runs(a.Markdown).Any(runs.Contains));
        return quoted is null ? reason : Neutral(quoted.Filename);
    }

    private static IEnumerable<string> Runs(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalise)
            .Where(w => w.Length > 0)
            .ToList();
        for (var i = 0; i + RunWords <= words.Count; i++)
        {
            yield return string.Join(' ', words.GetRange(i, RunWords));
        }
    }

    /// <summary>The word lower-cased, without the punctuation and symbols around it (markdown's <c>|</c>, <c>**</c>, quotes).</summary>
    private static string Normalise(string word)
    {
        var (start, end) = (0, word.Length);
        while (start < end && !char.IsLetterOrDigit(word[start]))
        {
            start++;
        }

        while (end > start && !char.IsLetterOrDigit(word[end - 1]))
        {
            end--;
        }

        return word[start..end].ToLowerInvariant();
    }
}
