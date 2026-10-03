using System.ComponentModel.DataAnnotations;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>Converter settings that are not user-facing, bound from the <c>Attachments</c> section.</summary>
public sealed class AttachmentOptions
{
    public const string SectionName = "Attachments";
    public const int DefaultMaxSheetRows = 200;

    /// <summary>Rows read per worksheet or CSV file, the header row included.</summary>
    [Range(1, 10_000)]
    public int MaxSheetRows { get; set; } = DefaultMaxSheetRows;
}
