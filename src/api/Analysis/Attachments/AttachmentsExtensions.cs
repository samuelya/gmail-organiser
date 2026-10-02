namespace GmailOrganiser.Analysis.Attachments;

public static class AttachmentsExtensions
{
    /// <summary>
    /// Registers the converters (as a list, first match wins), the conversion service, the policy and the prompt section. Needs
    /// <c>AddGmail</c> and <c>AddSettings</c>.
    /// </summary>
    public static IServiceCollection AddAttachments(this IServiceCollection services)
    {
        services.AddSingleton<IAttachmentConverter>(_ => new PdfAttachmentConverter());
        services.AddScoped<AttachmentConversionService>();
        services.AddScoped<IAttachmentPolicy, AttachmentPolicy>();
        services.AddOptions<AttachmentPromptOptions>()
            .BindConfiguration(AttachmentPromptOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<AttachmentPromptSection>();
        return services;
    }
}
