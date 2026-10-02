using Microsoft.Extensions.Options;

namespace GmailOrganiser.Analysis.Attachments;

public static class AttachmentsExtensions
{
    /// <summary>
    /// Registers the converters (as a list, first match wins), the conversion service and the policy. Needs
    /// <c>AddGmail</c> and <c>AddSettings</c>.
    /// </summary>
    public static IServiceCollection AddAttachments(this IServiceCollection services)
    {
        services.AddOptions<AttachmentOptions>()
            .BindConfiguration(AttachmentOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IAttachmentConverter>(_ => new PdfAttachmentConverter());
        services.AddSingleton<IAttachmentConverter>(_ => new WordDocumentAttachmentConverter());
        services.AddSingleton<IAttachmentConverter>(sp => new SpreadsheetAttachmentConverter(sp.GetRequiredService<IOptions<AttachmentOptions>>().Value.MaxSheetRows));
        services.AddSingleton<IAttachmentConverter>(sp => new CsvAttachmentConverter(sp.GetRequiredService<IOptions<AttachmentOptions>>().Value.MaxSheetRows));
        services.AddSingleton<IAttachmentConverter>(_ => new PresentationAttachmentConverter());
        services.AddSingleton<IAttachmentConverter, PlainTextAttachmentConverter>();
        services.AddScoped<AttachmentConversionService>();
        services.AddScoped<IAttachmentPolicy, AttachmentPolicy>();
        return services;
    }
}
