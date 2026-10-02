using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GmailOrganiser.Gmail;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>Bound from <c>Attachments:*</c>.</summary>
public sealed class AttachmentPromptOptions
{
    public const string SectionName = "Attachments";

    /// <summary>
    /// Characters of attachment text in one prompt, all its emails' attachments together, shared in email order (the
    /// per-email headings and the bodies are not counted; each attachment also keeps its own conversion cap).
    /// </summary>
    [Range(1, 1_000_000)]
    public int MaxTotalChars { get; set; } = 12_000;
}

/// <summary>One attachment of a message, in attachment order: either converted or skipped.</summary>
public sealed record PromptAttachment(ConvertedAttachment? Converted, SkippedAttachment? Skipped);

/// <summary>One prompt email's attachments in attachment order; <see cref="EmailNumber"/> matches its <c>### Email n</c> block.</summary>
public sealed record MessageAttachments(int EmailNumber, string EmailId, IReadOnlyList<PromptAttachment> Attachments);

/// <summary>
/// The attachment part of the analysis prompt (DESIGN §6.2, #68): converts a message's attachments and renders them
/// within <see cref="AttachmentPromptOptions.MaxTotalChars"/> for the whole prompt. The text exists only in the returned
/// values; it is never stored or logged.
/// </summary>
public sealed partial class AttachmentPromptSection(AttachmentConversionService conversion, IOptions<AttachmentPromptOptions> options)
{
    public const string Heading = "Attachments of the emails above, converted to text (the text may be truncated):";
    public const string Omitted = "[more attachments omitted]";
    public const string ContentStart = "<attachment_content>";
    public const string ContentEnd = "</attachment_content>";

    /// <summary>
    /// The message's attachments in attachment order, converted under <paramref name="snapshot"/>'s types and limits. A
    /// failing attachment is listed as <see cref="SkipReason.Failed"/>; Gmail connection and quota errors and
    /// cancellation propagate like a body fetch's. The caller checks the master switch.
    /// </summary>
    public async Task<IReadOnlyList<PromptAttachment>> ConvertAsync(
        string messageId, IReadOnlyList<GmailAttachment> attachments, AttachmentPolicySnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(snapshot);
        return attachments.Count == 0
            ? []
            : InOrder(attachments, await conversion.ConvertAllAsync(messageId, attachments, snapshot.EnabledTypes, snapshot.Limits, ct));
    }

    /// <summary>
    /// Interleaves the digest back into attachment order. The service keeps the order within each list and every
    /// attachment yields one entry with its filename and resolved type; same-named entries of one type may swap places.
    /// </summary>
    public static IReadOnlyList<PromptAttachment> InOrder(IReadOnlyList<GmailAttachment> attachments, AttachmentDigest digest)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(digest);
        var (c, s) = (0, 0);
        var ordered = new List<PromptAttachment>();
        foreach (var a in attachments)
        {
            var type = AttachmentTypeResolver.Resolve(a.MimeType, a.Filename);
            if (c < digest.Converted.Count && digest.Converted[c].Filename == a.Filename && digest.Converted[c].AttachmentType == type)
            {
                ordered.Add(new PromptAttachment(digest.Converted[c++], null));
            }
            else if (s < digest.Skipped.Count && digest.Skipped[s].Filename == a.Filename && digest.Skipped[s].AttachmentType == type)
            {
                ordered.Add(new PromptAttachment(null, digest.Skipped[s++]));
            }
        }

        // Anything not matched (never expected) still reaches the prompt, after the rest.
        ordered.AddRange(digest.Converted.Skip(c).Select(x => new PromptAttachment(x, null)));
        ordered.AddRange(digest.Skipped.Skip(s).Select(x => new PromptAttachment(null, x)));
        return ordered;
    }

    /// <summary>
    /// The <c>{{attachments}}</c> value: empty when no email has an attachment to show. The emails share
    /// <see cref="AttachmentPromptOptions.MaxTotalChars"/> in order; an email after the budget is spent gets no entry.
    /// </summary>
    public string Render(IReadOnlyList<MessageAttachments> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var budget = options.Value.MaxTotalChars;
        var parts = new List<string>();
        foreach (var m in messages.Where(m => m.Attachments.Count > 0))
        {
            var content = RenderMessage(m.Attachments, budget);
            if (content.Length == 0)
            {
                break;
            }

            parts.Add(string.Create(CultureInfo.InvariantCulture, $"Attachments of email {m.EmailNumber} ({OneLine(m.EmailId)}):\n") + content);
            budget -= content.Length;
        }

        return parts.Count == 0 ? string.Empty : Heading + "\n\n" + string.Join("\n\n", parts);
    }

    /// <summary>
    /// One message's attachments in attachment order, a block per converted one and a line per skipped one, at most
    /// <paramref name="maxTotalChars"/> characters (empty when not even <see cref="Omitted"/> fits). Over budget, every
    /// skipped line is kept first (they are short), then the blocks in order until one does not fit: that one is cut
    /// to the room left, the rest are dropped behind <see cref="Omitted"/>. The "Send to Claude" path (M4) renders a
    /// message with this method.
    /// </summary>
    public static string RenderMessage(IReadOnlyList<PromptAttachment> attachments, int maxTotalChars)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        // Each kept item costs its length plus one newline: the last one's pays for the marker's line, or is not written.
        var (kept, dropped) = Fit(attachments, maxTotalChars + 1);
        if (dropped)
        {
            (kept, _) = Fit(attachments, maxTotalChars - Omitted.Length);
        }

        var text = string.Join('\n', kept.Where(k => k is not null));
        return !dropped ? text
            : maxTotalChars < Omitted.Length ? string.Empty
            : text.Length == 0 ? Omitted
            : text + "\n" + Omitted;
    }

    /// <summary>
    /// The items that fit <paramref name="room"/> (each costs its length + 1; null where dropped) and whether any was
    /// dropped: skipped lines first, then blocks in order, the first that does not fit cut and the rest dropped.
    /// </summary>
    private static (string?[] Kept, bool Dropped) Fit(IReadOnlyList<PromptAttachment> attachments, int room)
    {
        var kept = new string?[attachments.Count];
        var stopped = false;
        var dropped = false;
        foreach (var (a, i) in attachments.Select((a, i) => (a, i)).Where(x => x.a.Skipped is not null))
        {
            var line = SkippedLine(a.Skipped!);
            stopped = stopped || line.Length + 1 > room;
            dropped |= stopped;
            kept[i] = stopped ? null : line;
            room -= stopped ? 0 : line.Length + 1;
        }

        foreach (var (a, i) in attachments.Select((a, i) => (a, i)).Where(x => x.a.Converted is not null))
        {
            var block = Block(a.Converted!);
            if (!stopped && block.Length + 1 <= room)
            {
                kept[i] = block;
                room -= block.Length + 1;
                continue;
            }

            kept[i] = stopped ? null : Cut(a.Converted!, room - 1);
            dropped |= kept[i] is null;
            stopped = true;
        }

        return (kept, dropped);
    }

    /// <summary>
    /// The block with its markdown cut so the whole block is at most <paramref name="room"/> characters, or null when
    /// that leaves no more than the truncation marker. Defusing keeps lengths, so the cut is measured on the raw markdown.
    /// </summary>
    private static string? Cut(ConvertedAttachment a, int room)
    {
        var markdownRoom = room - Block(a with { Markdown = "" }).Length;
        if (markdownRoom <= ConversionLimits.TruncatedMarker.Length)
        {
            return null;
        }

        var (markdown, _) = new ConversionLimits(0, 0, markdownRoom, 0).Truncate(a.Markdown);
        return Block(a with { Markdown = markdown });
    }

    private static string Block(ConvertedAttachment a) =>
        string.Create(CultureInfo.InvariantCulture, $"### Attachment: {OneLine(a.Filename)} ({TypeName(a.AttachmentType)})\n")
        + ContentStart + "\n" + Defuse(a.Markdown) + "\n" + ContentEnd;

    private static string SkippedLine(SkippedAttachment a) =>
        string.Create(CultureInfo.InvariantCulture, $"Skipped attachment: {OneLine(a.Filename)} ({TypeName(a.AttachmentType)}, {ReasonText(a.SkipReason)})");

    private static string TypeName(AttachmentType type) => type.ToString().ToLowerInvariant();

    private static string ReasonText(SkipReason reason) => reason switch
    {
        SkipReason.Disabled => "type not enabled",
        SkipReason.TooLarge => "too large",
        SkipReason.Unsupported => "unsupported type",
        SkipReason.Failed => "could not be read",
        SkipReason.TooMany => "too many attachments",
        _ => "skipped",
    };

    /// <summary>Filenames and ids stay on one line, so they can't start a fake block.</summary>
    private static string OneLine(string value) =>
        Defuse(string.Join(' ', value.Split(['\r', '\n', '\v', '\f', '\u0085', '\u2028', '\u2029'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));

    /// <summary>Attachment text can't open or close a content block: every <c>attachment_content</c> tag loses its <c>&lt;</c>.</summary>
    private static string Defuse(string value) => ContentTagRegex().Replace(value, "[$1");

    [GeneratedRegex(@"<(\s*/?\s*attachment_content)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContentTagRegex();
}
