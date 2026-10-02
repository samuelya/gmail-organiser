namespace GmailOrganiser.Analysis.Attachments;

public static class AttachmentsExtensions
{
    /// <summary>
    /// Registers the converters (as a list, first match wins), the conversion service and the policy. Needs
    /// <c>AddGmail</c> and <c>AddSettings</c>.
    /// </summary>
    public static IServiceCollection AddAttachments(this IServiceCollection services)
    {
        services.AddSingleton<IAttachmentConverter>(_ => new PdfAttachmentConverter());
        services.AddScoped<AttachmentConversionService>();
        services.AddScoped<IAttachmentPolicy, AttachmentPolicy>();
        return services;
    }
}
