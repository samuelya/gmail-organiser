namespace GmailOrganiser.Analysis.Attachments;

public static class AttachmentsExtensions
{
    /// <summary>Registers the converters (as a list, first match wins) and the conversion service. Needs <c>AddGmail</c>.</summary>
    public static IServiceCollection AddAttachments(this IServiceCollection services)
    {
        services.AddSingleton<IAttachmentConverter, PdfAttachmentConverter>();
        services.AddScoped<AttachmentConversionService>();
        return services;
    }
}
