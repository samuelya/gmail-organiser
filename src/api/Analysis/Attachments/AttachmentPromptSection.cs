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

    /// <summary>Characters of attachment text per message in the prompt, all attachments together (the body has its own cap).</summary>
    [Range(1, 1_000_000)]
    public int MaxTotalChars { get; set; } = 12_000;
}

/// <summary>One prompt email's converted attachments; <see cref="EmailNumber"/> matches its <c>### Email n</c> block.</summary>
public sealed record MessageAttachments(int EmailNumber, string EmailId, AttachmentDigest Digest);

/// <summary>
/// The attachment part of the analysis prompt (DESIGN §6.2, #68): converts a message's attachments under the policy and
/// renders the digests within <see cref="AttachmentPromptOptions.MaxTotalChars"/> per message. The text exists only in
/// the returned values; it is never stored or logged.
/// </summary>
public sealed partial class AttachmentPromptSection(
    IAttachmentPolicy policy,
    AttachmentConversionService conversion,
    IOptions<AttachmentPromptOptions> options,
    ILogger<AttachmentPromptSection> logger)
{
    public const string Heading = "Attachments of the emails above, converted to text (the text may be truncated):";
    public const string Omitted = "[more attachments omitted]";
    public const string ContentStart = "<attachment_content>";
    public const string ContentEnd = "</attachment_content>";

    public Task<AttachmentPolicySnapshot> GetPolicyAsync(CancellationToken ct) => policy.GetAsync(ct);

    /// <summary>
    /// The digest of one message, or <see cref="AttachmentDigest.Empty"/> when the master switch is off. An unexpected
    /// failure lists every attachment as <see cref="SkipReason.Failed"/>; Gmail connection and quota errors and
    /// cancellation propagate like a body fetch's.
    /// </summary>
    public async Task<AttachmentDigest> ConvertAsync(
        string messageId, IReadOnlyList<GmailAttachment> attachments, AttachmentPolicySnapshot snapshot, CancellationToken ct)
    {
        if (!snapshot.Enabled || attachments.Count == 0)
        {
            return AttachmentDigest.Empty;
        }

        try
        {
            return await conversion.ConvertAllAsync(messageId, attachments, snapshot.EnabledTypes, snapshot.Limits, ct);
        }
        catch (Exception ex) when (ex is not (GmailNotConnectedException or GmailRateLimitedException) && !ct.IsCancellationRequested)
        {
            LogConversionFailed(logger, ex.GetType().Name);
            return new AttachmentDigest(
                [],
                [.. attachments.Select(a => new SkippedAttachment(a.Filename, AttachmentTypeResolver.Resolve(a.MimeType, a.Filename), SkipReason.Failed))]);
        }
    }

    /// <summary>The <c>{{attachments}}</c> value: empty when no email has an attachment to show.</summary>
    public string Render(IReadOnlyList<MessageAttachments> messages)
    {
        var parts = messages
            .Where(m => m.Digest.Converted.Count + m.Digest.Skipped.Count > 0)
            .Select(m => string.Create(CultureInfo.InvariantCulture, $"Attachments of email {m.EmailNumber} ({OneLine(m.EmailId)}):\n")
                + RenderMessage(m.Digest, options.Value.MaxTotalChars))
            .ToList();
        return parts.Count == 0 ? string.Empty : Heading + "\n\n" + string.Join("\n\n", parts);
    }

    /// <summary>
    /// One message's attachments: a block per converted attachment, then a line per skipped one, in order, at most
    /// <paramref name="maxTotalChars"/> characters; what does not fit is dropped behind <see cref="Omitted"/>. Only the
    /// first block is cut to fit, so a per-attachment limit above the total still shows something. The "Send to Claude"
    /// path (M4) renders the same digest with this method.
    /// </summary>
    public static string RenderMessage(AttachmentDigest digest, int maxTotalChars)
    {
        ArgumentNullException.ThrowIfNull(digest);
        var items = digest.Converted.Select(Block).Concat(digest.Skipped.Select(SkippedLine)).ToList();
        var sb = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var item = i == 0 ? FitFirst(digest, items[0], maxTotalChars) : items[i];
            var separator = sb.Length == 0 ? 0 : 1;
            if (sb.Length + separator + item.Length > maxTotalChars)
            {
                return sb.Append(sb.Length == 0 ? "" : "\n").Append(Omitted).ToString();
            }

            sb.Append(separator == 0 ? "" : "\n").Append(item);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The first block, its markdown cut so the whole block fits <paramref name="maxTotalChars"/> when that leaves room
    /// for more than the marker. Defusing keeps lengths, so the cut can be measured on the raw markdown.
    /// </summary>
    private static string FitFirst(AttachmentDigest digest, string item, int maxTotalChars)
    {
        if (item.Length <= maxTotalChars || digest.Converted.Count == 0)
        {
            return item;
        }

        var first = digest.Converted[0];
        var room = maxTotalChars - Block(first with { Markdown = "" }).Length;
        if (room <= ConversionLimits.TruncatedMarker.Length)
        {
            return item;
        }

        var (markdown, _) = new ConversionLimits(0, 0, room, 0).Truncate(first.Markdown);
        return Block(first with { Markdown = markdown });
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Converting a message's attachments failed with {ExceptionType}; all listed as skipped")]
    private static partial void LogConversionFailed(ILogger logger, string exceptionType);
}
