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
        var styles = main.StyleDefinitionsPart?.Styles;
        var headings = HeadingLevels(styles);
        var listStyles = ReadListStyles(styles);
        var markdown = new StringBuilder();

        // The body is streamed block by block, so a huge document costs only the blocks read before the limit.
        using var reader = OpenXmlReader.Create(main);
        while (reader.Read())
        {
            if (!reader.IsStartElement || (reader.ElementType != typeof(Paragraph) && reader.ElementType != typeof(Table) && reader.ElementType != typeof(SdtBlock)))
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            foreach (var block in Blocks(reader.LoadCurrentElement()))
            {
                foreach (var text in block switch
                {
                    Paragraph paragraph => ParagraphMarkdown(paragraph, headings, listStyles),
                    Table table => [TableMarkdown(table)],
                    _ => [],
                })
                {
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
                        return markdown.ToString();
                    }
                }
            }
        }

        return markdown.ToString();
    }

    /// <summary>Paragraphs and tables in document order, looking inside block-level content controls.</summary>
    private static IEnumerable<OpenXmlElement> Blocks(OpenXmlElement? element)
    {
        if (element is SdtBlock sdt)
        {
            foreach (var child in sdt.SdtContentBlock?.ChildElements ?? [])
            {
                foreach (var inner in Blocks(child))
                {
                    yield return inner;
                }
            }
        }
        else if (element is Paragraph or Table)
        {
            yield return element;
        }
    }

    /// <summary>The paragraph as a heading, list item or plain text, then the paragraphs of its text boxes.</summary>
    private static List<string> ParagraphMarkdown(Paragraph paragraph, IReadOnlyDictionary<string, int> headings, ListStyles listStyles)
    {
        var boxes = new List<string>();
        var text = ParagraphText(paragraph, boxes).Trim();
        var properties = paragraph.ParagraphProperties;
        var style = properties?.ParagraphStyleId?.Val?.Value;
        if (text.Length == 0)
        {
            // Nothing in the host paragraph itself.
        }
        else if (style is not null && headings.TryGetValue(style, out var level))
        {
            text = new string('#', level) + " " + text.ReplaceLineEndings(" ");
        }
        else if (properties?.NumberingProperties?.NumberingId?.Val?.Value is { } numId ? numId != 0 : listStyles.IsList(style))
        {
            // numId 0 switches numbering off; without a numId on the paragraph its style decides.
            text = "- " + text;
        }

        return [text, .. boxes];
    }

    /// <summary>
    /// The paragraph's own text. Of an <c>mc:AlternateContent</c> only the first choice (or else the fallback) is read,
    /// so a text box isn't read twice; text-box paragraphs go to <paramref name="boxes"/>, not into the host's text.
    /// </summary>
    private static string ParagraphText(OpenXmlElement paragraph, List<string> boxes)
    {
        var text = new StringBuilder();
        AppendText(paragraph, text, boxes);
        return text.ToString();
    }

    private static void AppendText(OpenXmlElement element, StringBuilder text, List<string> boxes)
    {
        foreach (var child in element.ChildElements)
        {
            switch (child)
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
                case AlternateContent alternate:
                    if ((alternate.GetFirstChild<AlternateContentChoice>() ?? (OpenXmlElement?)alternate.GetFirstChild<AlternateContentFallback>()) is { } branch)
                    {
                        AppendText(branch, text, boxes);
                    }

                    break;
                case TextBoxContent box:
                    foreach (var inner in OwnParagraphs(box))
                    {
                        var boxText = ParagraphText(inner, boxes).Trim();
                        if (boxText.Length > 0)
                        {
                            boxes.Add(boxText);
                        }
                    }

                    break;
                default:
                    AppendText(child, text, boxes);
                    break;
            }
        }
    }

    /// <summary>Paragraphs of <paramref name="root"/>, its tables included, but not those of a text box inside it.</summary>
    private static IEnumerable<Paragraph> OwnParagraphs(OpenXmlElement root) =>
        root.Descendants<Paragraph>().Where(p => !p.Ancestors().TakeWhile(a => a != root).OfType<TextBoxContent>().Any());

    private static string TableMarkdown(Table table)
    {
        var rows = table.Elements<TableRow>()
            .Select(row => (IReadOnlyList<string>)[.. row.Elements<TableCell>().Select(CellText)])
            .ToList();
        var markdown = new StringBuilder();
        MarkdownTable.Append(markdown, rows);
        return markdown.ToString().TrimEnd();
    }

    private static string CellText(TableCell cell)
    {
        var parts = new List<string>();
        foreach (var paragraph in OwnParagraphs(cell))
        {
            var boxes = new List<string>();
            parts.Add(ParagraphText(paragraph, boxes).Trim());
            parts.AddRange(boxes);
        }

        return string.Join(" ", parts.Where(p => p.Length > 0));
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

    /// <summary>
    /// Paragraph styles that number their paragraphs (such as <c>List Bullet</c> and <c>List Number</c>): a
    /// <c>numPr</c> with a non-zero <c>numId</c> on the style or the nearest style it is based on that has one.
    /// </summary>
    private static ListStyles ReadListStyles(Styles? styles)
    {
        var paragraphStyles = styles?.Elements<Style>().Where(s => s.StyleId?.Value is not null && (s.Type?.Value ?? StyleValues.Paragraph) == StyleValues.Paragraph).ToList() ?? [];
        var byId = paragraphStyles.GroupBy(s => s.StyleId!.Value!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in byId.Keys)
        {
            var current = byId[id];
            for (var depth = 0; current is not null && depth < 20; depth++)
            {
                if (current.StyleParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value is { } numId)
                {
                    if (numId != 0)
                    {
                        listed.Add(id);
                    }

                    break;
                }

                current = current.BasedOn?.Val?.Value is { } parent ? byId.GetValueOrDefault(parent) : null;
            }
        }

        var defaultStyle = paragraphStyles.FirstOrDefault(s => s.Default?.Value == true)?.StyleId?.Value;
        return new ListStyles(listed, defaultStyle);
    }

    private sealed record ListStyles(IReadOnlySet<string> Listed, string? DefaultStyle)
    {
        /// <summary>Whether a paragraph with this style (none: the default paragraph style) is a list item.</summary>
        public bool IsList(string? style) => (style ?? DefaultStyle) is { } id && Listed.Contains(id);
    }
}
