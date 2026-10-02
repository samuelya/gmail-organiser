using System.Text.RegularExpressions;

namespace GmailOrganiser.Analysis.Grouping;

/// <summary>
/// Turns a subject into a template so "Your order 1234 shipped" and "Your order 5678 shipped" group together:
/// variable parts (numbers, dates, times, amounts, percentages, references) become <c>#</c>. Deterministic.
/// </summary>
public static partial class SubjectNormaliser
{
    public const int MaxLength = 120;

    // Bounds the regex work for pathological subjects; the template is capped far below this anyway.
    private const int MaxInputLength = 1000;

    public static string Template(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return "";
        }

        var s = subject.Length > MaxInputLength ? subject[..MaxInputLength] : subject;
        s = s.ToLowerInvariant();
        s = Prefixes().Replace(s, "");
        s = BracketTags().Replace(s, " ");
        s = IsoDateTime().Replace(s, "#");
        s = LocalDate().Replace(s, "#");
        s = Time().Replace(s, "#");
        s = Currency().Replace(s, "#");
        s = Percentage().Replace(s, "#");
        s = Reference().Replace(s, "#");
        s = Digits().Replace(s, "#");
        s = NumberGroups().Replace(s, "#");
        s = MonthNextToNumber().Replace(s, "#");
        s = Whitespace().Replace(s, " ").Trim();
        return s.Length > MaxLength ? s[..MaxLength].TrimEnd() : s;
    }

    // Repeated reply/forward prefixes (English and German), optionally counted ("re[2]:"), and leading tags.
    [GeneratedRegex(@"^(\s*((re|fwd?|aw|wg)\s*(\[\d+\])?\s*:|\[[^\]]{0,60}\]))+\s*", RegexOptions.CultureInvariant)]
    private static partial Regex Prefixes();

    [GeneratedRegex(@"\[[^\]]{0,60}\]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketTags();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}(t\d{2}:\d{2}(:\d{2})?(\.\d+)?(z|[+-]\d{2}:?\d{2})?)?", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDateTime();

    [GeneratedRegex(@"\b\d{1,2}[./-]\d{1,2}[./-]\d{2,4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LocalDate();

    [GeneratedRegex(@"\b\d{1,2}:\d{2}(:\d{2})?(\s*[ap]\.?m\.?\b)?", RegexOptions.CultureInvariant)]
    private static partial Regex Time();

    [GeneratedRegex(@"[$€£¥]\s?\d[\d.,']*|\d[\d.,']*\s?([$€£¥]|\b(usd|eur|gbp|chf)\b)", RegexOptions.CultureInvariant)]
    private static partial Regex Currency();

    [GeneratedRegex(@"\d[\d.,]*\s?%", RegexOptions.CultureInvariant)]
    private static partial Regex Percentage();

    [GeneratedRegex(@"#\s?\d+|\b(no|nr)\.?\s?\d+", RegexOptions.CultureInvariant)]
    private static partial Regex Reference();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    // "#.#", "#,#", "#-#" and "##" left over from the replacements above collapse into one placeholder.
    [GeneratedRegex(@"#+([.,:/'-]?#+)*", RegexOptions.CultureInvariant)]
    private static partial Regex NumberGroups();

    [GeneratedRegex(
        @"#\.?\s+(jan(uary)?|feb(ruary)?|mar(ch)?|apr(il)?|may|june?|july?|aug(ust)?|sept?(ember)?|oct(ober)?|nov(ember)?|dec(ember)?)\b\.?(\s+#)?|\b(jan(uary)?|feb(ruary)?|mar(ch)?|apr(il)?|may|june?|july?|aug(ust)?|sept?(ember)?|oct(ober)?|nov(ember)?|dec(ember)?)\.?\s+#",
        RegexOptions.CultureInvariant)]
    private static partial Regex MonthNextToNumber();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
