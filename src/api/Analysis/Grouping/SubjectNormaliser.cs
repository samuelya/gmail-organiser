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
        s = DayMonthYear().Replace(s, "#");
        s = MonthDayOrYear().Replace(s, "#");
        s = DayUnambiguousMonth().Replace(s, "#");
        s = Time().Replace(s, "#");
        s = Currency().Replace(s, "#");
        s = Percentage().Replace(s, "#");
        s = Reference().Replace(s, "#");
        s = Digits().Replace(s, "#");
        s = NumberGroups().Replace(s, "#");
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

    // Month words that are also common words ("may", "march") only count with a day and a year, or before a day or year;
    // "order 12 may be delayed" keeps its "may". Applied before digits become placeholders, on the raw numbers.
    private const string Month =
        "(jan(uary)?|feb(ruary)?|mar(ch)?|apr(il)?|may|june?|july?|aug(ust)?|sept?(ember)?|oct(ober)?|nov(ember)?|dec(ember)?)";

    private const string UnambiguousMonth =
        "(jan(uary)?|feb(ruary)?|mar|apr(il)?|jun|jul|aug(ust)?|sept?(ember)?|oct(ober)?|nov(ember)?|dec(ember)?)";

    private const string Day = @"\d{1,2}(st|nd|rd|th)?";

    // "3 march 2026", "3rd may, 2026".
    [GeneratedRegex(@"\b" + Day + @"\.?\s+" + Month + @"\.?,?\s+\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex DayMonthYear();

    // "mar 3", "may 3rd, 2026", "march 2026".
    [GeneratedRegex(@"\b" + Month + @"\.?\s+(" + Day + @"\b(,?\s+\d{4}\b)?|\d{4}\b)", RegexOptions.CultureInvariant)]
    private static partial Regex MonthDayOrYear();

    // "3 aug", "12. oct".
    [GeneratedRegex(@"\b" + Day + @"\.?\s+" + UnambiguousMonth + @"\b\.?", RegexOptions.CultureInvariant)]
    private static partial Regex DayUnambiguousMonth();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
