using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GmailOrganiser.Gmail;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// The body of a docx document as markdown (Open XML SDK, MIT): paragraphs in order, built-in headings and the title
/// as <c>#</c> headings, numbered and bulleted paragraphs as list items, tables as markdown tables. Headers, footers,
/// comments and tracked deletions are left out. Legacy doc, odt and rtf aren't Open XML and throw, which the caller
/// records as failed. Reading stops once the text exceeds <see cref="ConversionLimits.MaxChars"/>.
/// </summary>
/// <param name="parseTimeout">Defaults to 30 s; a test seam.</param>
public sealed class WordDocumentAttachmentConverter(TimeSpan? parseTimeout = null) : IAttachmentConverter
{
    private readonly TimeSpan parseTimeout = parseTimeout ?? ParseTimeout.Default;

    public bool CanConvert(AttachmentType type) => type == AttachmentType.WordDocument;

    public async Task<ConvertedAttachment> ConvertAsync(GmailAttachment attachment, Stream content, ConversionLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);

        var markdown = await ParseTimeout.RunAsync(t => Read(content, limits.MaxChars, t), parseTimeout, "document", ct);
        return new ConvertedAttachment(attachment.Filename, AttachmentType.WordDocument, markdown, false);
    }

    private static string Read(Stream content, int maxChars, CancellationToken ct)
    {
        using var document = WordprocessingDocument.Open(content, false, OfficeDocument.ReadOnly);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("The document has no main part.");
        var headings = HeadingLevels(main.StyleDefinitionsPart?.Styles);
        var markdown = new StringBuilder();
        foreach (var block in Blocks(main.Document?.Body))
        {
            ct.ThrowIfCancellationRequested();
            var text = block switch
            {
                Paragraph paragraph => ParagraphMarkdown(paragraph, headings),
                Table table => TableMarkdown(table),
                _ => "",
            };
            if (text.Length == 0)
            {
                continue;
            }

            if (markdown.Length > 0)
            {
                markdown.Append("\n\n");
            }

            markdown.Append(text);
            if (markdown.Length > maxChars)
            {
                break;
            }
        }

        return markdown.ToString();
    }

    /// <summary>Paragraphs and tables in document order, looking inside block-level content controls.</summary>
    private static IEnumerable<OpenXmlElement> Blocks(OpenXmlElement? parent)
    {
        foreach (var child in parent?.ChildElements ?? [])
        {
            if (child is SdtBlock sdt)
            {
                foreach (var inner in Blocks(sdt.SdtContentBlock))
                {
                    yield return inner;
                }
            }
            else if (child is Paragraph or Table)
            {
                yield return child;
            }
        }
    }

    private static string ParagraphMarkdown(Paragraph paragraph, IReadOnlyDictionary<string, int> headings)
    {
        var text = ParagraphText(paragraph).Trim();
        if (text.Length == 0)
        {
            return "";
        }

        var properties = paragraph.ParagraphProperties;
        if (properties?.ParagraphStyleId?.Val?.Value is { } style && headings.TryGetValue(style, out var level))
        {
            return new string('#', level) + " " + text.ReplaceLineEndings(" ");
        }

        return properties?.NumberingProperties is not null ? "- " + text : text;
    }

    private static string ParagraphText(OpenXmlElement paragraph)
    {
        var text = new StringBuilder();
        foreach (var element in paragraph.Descendants())
        {
            switch (element)
            {
                case Text t:
                    text.Append(t.Text);
                    break;
                case TabChar:
                    text.Append(' ');
                    break;
                case Break or CarriageReturn:
                    text.Append('\n');
                    break;
            }
        }

        return text.ToString();
    }

    private static string TableMarkdown(Table table)
    {
        var rows = table.Elements<TableRow>()
            .Select(row => (IReadOnlyList<string>)[.. row.Elements<TableCell>().Select(cell =>
                string.Join(" ", cell.Descendants<Paragraph>().Select(p => ParagraphText(p).Trim()).Where(p => p.Length > 0)))])
            .ToList();
        var markdown = new StringBuilder();
        MarkdownTable.Append(markdown, rows);
        return markdown.ToString().TrimEnd();
    }

    /// <summary>
    /// Style ID to heading level, from the built-in style names (<c>Title</c>, <c>heading 1</c> … <c>heading 6</c>),
    /// which stay English in every UI language while the IDs are localised.
    /// </summary>
    private static Dictionary<string, int> HeadingLevels(Styles? styles)
    {
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var style in styles?.Elements<Style>() ?? [])
        {
            var name = style.StyleName?.Val?.Value?.Trim().ToLowerInvariant();
            var level = name switch
            {
                "title" => 1,
                _ when name is not null && name.StartsWith("heading ", StringComparison.Ordinal)
                    && int.TryParse(name.AsSpan(8), out var n) && n is >= 1 and <= 6 => n,
                _ => 0,
            };
            if (level > 0 && style.StyleId?.Value is { } id)
            {
                levels[id] = level;
            }
        }

        return levels;
    }
}
