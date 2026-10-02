namespace GmailOrganiser.Analysis.Attachments;

/// <summary>What an attachment is, resolved by <see cref="AttachmentTypeResolver"/>; settings enable conversion per type.</summary>
public enum AttachmentType
{
    Pdf,
    Image,
    Spreadsheet,
    Csv,
    WordDocument,
    Presentation,
    PlainText,
    Archive,
    Other,
}
