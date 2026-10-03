using System.Text.Json;
using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailOrganiser.Tests.Unit;

public sealed class AttachmentSettingsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Defaults_enable_the_easy_types_and_not_archives_or_other()
    {
        var settings = new AttachmentSettings();

        settings.Enabled.ShouldBeTrue();
        settings.Types.Select(t => t.Type).ShouldBe(Enum.GetValues<AttachmentType>());
        settings.EnabledTypes().ShouldBe(
            [AttachmentType.Pdf, AttachmentType.Image, AttachmentType.Spreadsheet, AttachmentType.Csv,
             AttachmentType.WordDocument, AttachmentType.Presentation, AttachmentType.PlainText], ignoreOrder: true);
        settings.ToLimits().ShouldBe(new ConversionLimits(
            10 * 1024 * 1024, 10 * 1024 * 1024, 4000, 5, new ImageReading(ImageMode.Ocr, null, AppSettings.FallbackOllamaBaseUrl)));
    }

    [Theory]
    [InlineData(AttachmentType.WordDocument, "\"word_document\"")]
    [InlineData(AttachmentType.PlainText, "\"plain_text\"")]
    [InlineData(AttachmentType.Pdf, "\"pdf\"")]
    public void Types_round_trip_as_snake_case(AttachmentType type, string json)
    {
        JsonSerializer.Serialize(type, Json).ShouldBe(json);
        JsonSerializer.Deserialize<AttachmentType>(json, Json).ShouldBe(type);
    }

    [Fact]
    public void Settings_round_trip_through_json()
    {
        var settings = new AttachmentSettings
        {
            Enabled = false,
            Types = AttachmentSettings.WithDefaults([new AttachmentTypeSetting(AttachmentType.Image, false)]),
            MaxBytes = 65_536,
            MaxImageBytes = 1_000_000,
            MaxChars = 500,
            MaxPerMessage = 20,
        };

        var copy = JsonSerializer.Deserialize<AttachmentSettings>(JsonSerializer.Serialize(settings, Json), Json).ShouldNotBeNull();

        copy.ShouldBe(settings with { Types = copy.Types });
        copy.Types.ShouldBe(settings.Types);
    }

    [Fact]
    public void Saved_type_list_drops_unknown_entries_merges_defaults_and_keeps_archives_off()
    {
        const string json = """
            {"types":[{"type":"pdf","enabled":false},{"type":"hologram","enabled":true},{"type":"archive","enabled":true},
             {"type":3,"enabled":true},{"type":"image"},"csv",{"type":"pdf","enabled":true}]}
            """;

        var settings = JsonSerializer.Deserialize<AttachmentSettings>(json, Json).ShouldNotBeNull();

        settings.Types.Select(t => t.Type).ShouldBe(Enum.GetValues<AttachmentType>());
        settings.Types.ShouldAllBe(t => t.Enabled == (t.Type != AttachmentType.Pdf && AttachmentSettings.IsEnabledByDefault(t.Type)));
        settings.MaxChars.ShouldBe(AttachmentSettings.DefaultMaxChars);
    }

    [Fact]
    public void Master_switch_off_enables_no_type()
    {
        new AttachmentSettings { Enabled = false }.EnabledTypes().ShouldBeEmpty();
    }

    [Fact]
    public void Image_limit_applies_to_images_only()
    {
        var limits = new ConversionLimits(MaxBytes: 100, MaxImageBytes: 200, MaxChars: 500, MaxPerMessage: 1);

        limits.MaxBytesFor(AttachmentType.Image).ShouldBe(200);
        limits.MaxBytesFor(AttachmentType.Pdf).ShouldBe(100);
    }

    public static TheoryData<UpdateAttachmentSettingsRequest, string> InvalidRequests => new()
    {
        { new(MaxBytes: SettingsValidation.MinAttachmentMaxBytes - 1), "attachments.maxBytes" },
        { new(MaxBytes: SettingsValidation.MaxAttachmentMaxBytes + 1), "attachments.maxBytes" },
        { new(MaxImageBytes: 0), "attachments.maxImageBytes" },
        { new(MaxChars: 499), "attachments.maxChars" },
        { new(MaxChars: 50_001), "attachments.maxChars" },
        { new(MaxPerMessage: 0), "attachments.maxPerMessage" },
        { new(MaxPerMessage: 21), "attachments.maxPerMessage" },
        { new(Types: [new("hologram", true)]), "attachments.types[0].type" },
        { new(Types: [new("Pdf", true)]), "attachments.types[0].type" },
        { new(Types: [new(null, true)]), "attachments.types[0].type" },
        { new(Types: [new("pdf", true), new("pdf", false)]), "attachments.types[1].type" },
        { new(Types: [new("csv", null)]), "attachments.types[0].enabled" },
        { new(Types: [new("pdf", true), new("archive", true)]), "attachments.types[1].enabled" },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Invalid_attachment_settings_are_field_errors(UpdateAttachmentSettingsRequest attachments, string field)
    {
        var errors = SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, Attachments: attachments));

        errors.Keys.ShouldBe([field]);
    }

    [Fact]
    public void Edge_values_and_archive_off_are_valid()
    {
        var errors = SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, Attachments: new(
            Enabled: false,
            Types: [new("archive", false), new("other", true)],
            MaxBytes: SettingsValidation.MinAttachmentMaxBytes,
            MaxImageBytes: SettingsValidation.MaxAttachmentMaxBytes,
            MaxChars: SettingsValidation.MaxAttachmentMaxChars,
            MaxPerMessage: SettingsValidation.MinAttachmentMaxPerMessage)));

        errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task Policy_returns_the_effective_types_and_limits()
    {
        var store = new FixedSettingsStore(new AppSettings
        {
            Attachments = new AttachmentSettings { MaxImageBytes = 2_000_000, Types = AttachmentSettings.WithDefaults([new(AttachmentType.Csv, false)]) },
        });

        var policy = await new AttachmentPolicy(store, NullLogger<AttachmentPolicy>.Instance).GetAsync(TestContext.Current.CancellationToken);

        policy.Enabled.ShouldBeTrue();
        policy.EnabledTypes.ShouldNotContain(AttachmentType.Csv);
        policy.EnabledTypes.ShouldContain(AttachmentType.Pdf);
        policy.Limits.MaxImageBytes.ShouldBe(2_000_000);
    }

    private sealed class FixedSettingsStore(AppSettings settings) : ISettingsStore
    {
        public Task<AppSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(settings);

        public Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
