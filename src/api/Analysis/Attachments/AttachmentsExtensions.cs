using Microsoft.Extensions.Options;

namespace GmailOrganiser.Analysis.Attachments;

public static class AttachmentsExtensions
{
    /// <summary>
    /// Registers the converters (as a list, first match wins), the conversion service, the policy and the prompt section. Needs
    /// <c>AddGmail</c>, <c>AddSettings</c> and <c>AddLlm</c>.
    /// </summary>
    public static IServiceCollection AddAttachments(this IServiceCollection services)
    {
        services.AddOptions<AttachmentOptions>()
            .BindConfiguration(AttachmentOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IOcrEngine, TesseractOcrEngine>();
        // Scoped like ILlmClientFactory, which the vision client uses.
        services.AddScoped<IVisionClient, OllamaVisionClient>();
        services.AddScoped<ImageTextReader>();
        services.AddScoped<IAttachmentConverter>(sp => new PdfAttachmentConverter(
            scanReader: sp.GetRequiredService<ImageTextReader>(),
            maxOcrPages: sp.GetRequiredService<IOptions<AttachmentOptions>>().Value.MaxOcrPages,
            logger: sp.GetRequiredService<ILogger<PdfAttachmentConverter>>()));
        services.AddSingleton<IAttachmentConverter>(_ => new WordDocumentAttachmentConverter());
        services.AddSingleton<IAttachmentConverter>(sp => new SpreadsheetAttachmentConverter(sp.GetRequiredService<IOptions<AttachmentOptions>>().Value.MaxSheetRows));
        services.AddSingleton<IAttachmentConverter>(sp => new CsvAttachmentConverter(sp.GetRequiredService<IOptions<AttachmentOptions>>().Value.MaxSheetRows));
        services.AddSingleton<IAttachmentConverter>(_ => new PresentationAttachmentConverter());
        services.AddSingleton<IAttachmentConverter, PlainTextAttachmentConverter>();
        services.AddScoped<IAttachmentConverter, ImageAttachmentConverter>();
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
