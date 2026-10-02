using System.Text;
using DocumentFormat.OpenXml.Packaging;
using GmailOrganiser.Gmail;
using Drawing = DocumentFormat.OpenXml.Drawing;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// The slide text of a pptx presentation as markdown (Open XML SDK, MIT): per slide in show order a
/// <c>## Slide n</c> heading and one line per non-empty text paragraph. Speaker notes and images are left out; slides
/// without text are skipped. Legacy ppt and odp throw, which the caller records as failed. Reading stops once the text
/// exceeds <see cref="ConversionLimits.MaxChars"/>.
/// </summary>
/// <param name="parseTimeout">Defaults to 30 s; a test seam.</param>
public sealed class PresentationAttachmentConverter(TimeSpan? parseTimeout = null) : IAttachmentConverter
{
    private readonly TimeSpan parseTimeout = parseTimeout ?? ParseTimeout.Default;

    public bool CanConvert(AttachmentType type) => type == AttachmentType.Presentation;

    public async Task<ConvertedAttachment> ConvertAsync(GmailAttachment attachment, Stream content, ConversionLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);

        var markdown = await ParseTimeout.RunAsync(t => Read(content, limits.MaxChars, t), parseTimeout, "presentation", ct);
        return new ConvertedAttachment(attachment.Filename, AttachmentType.Presentation, markdown, false);
    }

    private static string Read(Stream content, int maxChars, CancellationToken ct)
    {
        using var document = PresentationDocument.Open(content, false, OfficeDocument.ReadOnly);
        var presentation = document.PresentationPart ?? throw new InvalidDataException("The presentation has no presentation part.");
        var slideIds = presentation.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>() ?? [];
        var markdown = new StringBuilder();
        var number = 0;
        foreach (var slideId in slideIds)
        {
            ct.ThrowIfCancellationRequested();
            number++;
            if (slideId.RelationshipId?.Value is not { } id || presentation.GetPartById(id) is not SlidePart slide)
            {
                continue;
            }

            var lines = slide.Slide?.Descendants<Drawing.Paragraph>()
                .Select(p => string.Concat(p.Descendants<Drawing.Text>().Select(t => t.Text)).Trim())
                .Where(line => line.Length > 0)
                .ToList() ?? [];
            if (lines.Count == 0)
            {
                continue;
            }

            if (markdown.Length > 0)
            {
                markdown.Append("\n\n");
            }

            markdown.Append("## Slide ").Append(number).Append("\n\n").AppendJoin('\n', lines);
            if (markdown.Length > maxChars)
            {
                break;
            }
        }

        return markdown.ToString();
    }
}
