using GmailOrganiser.Analysis;
using GmailOrganiser.Data;

namespace GmailOrganiser.Settings;

/// <summary>
/// The retention sweep (block <c>retention</c> of <see cref="AppSettings"/>, #368): applied mail older than its mail
/// type's days gets the delete label and leaves the inbox; a policy's or sub-rule's <c>RetentionDays</c> wins. Off by default.
/// </summary>
public sealed record RetentionSettings
{
    public const int MinDays = 1;
    public const int MaxDays = 3650;

    public bool Enabled { get; init; }

    /// <summary>
    /// Days to keep each mail type, every type listed; <c>0</c> = keep. Stored as <c>0</c>, not <c>null</c>: the settings
    /// overlay ignores a saved <c>null</c> over a default that has a value. The API shows <c>0</c> as <c>null</c>.
    /// </summary>
    public IReadOnlyDictionary<MailType, int> Days { get; init; } = DefaultDays;

    /// <summary>Owner-accepted defaults (#340); Marketing's 30 is a conservative placeholder (#371).</summary>
    public static IReadOnlyDictionary<MailType, int> DefaultDays { get; } = Enum.GetValues<MailType>().ToDictionary(t => t, t => t switch
    {
        MailType.SecurityOtp => 7,
        MailType.Notification => 90,
        MailType.Marketing => 30,
        MailType.Social => 90,
        MailType.Newsletter => 90,
        _ => 0,
    });

    /// <summary>Days to keep <paramref name="type"/>; null = keep.</summary>
    public int? DaysFor(MailType type) => Days.TryGetValue(type, out var days) && days > 0 ? days : null;
}

/// <param name="Days">Every mail type by its snake_case name; null = keep.</param>
public sealed record RetentionSettingsDto(bool Enabled, IReadOnlyDictionary<string, int?> Days)
{
    public static RetentionSettingsDto From(RetentionSettings s) => new(
        s.Enabled,
        Enum.GetValues<MailType>().ToDictionary(SnakeCaseEnumConverter<MailType>.ToDb, s.DaysFor));
}

/// <summary>
/// Partial update of <see cref="RetentionSettings"/>: <c>null</c> <see cref="Enabled"/> leaves it unchanged; <see cref="Days"/>
/// changes only the types it lists (snake_case names), a <c>null</c> value meaning keep.
/// </summary>
public sealed record UpdateRetentionSettingsRequest(bool? Enabled = null, IReadOnlyDictionary<string, int?>? Days = null)
{
    public static void Validate(Dictionary<string, string[]> errors, UpdateRetentionSettingsRequest request)
    {
        foreach (var (name, days) in request.Days ?? new Dictionary<string, int?>())
        {
            if (!SnakeCaseEnumConverter<MailType>.TryFromDb(name, out _))
            {
                errors[$"retention.days.{name}"] = [$"Unknown mail type; must be one of {SnakeCaseEnumConverter<MailType>.NamesList}."];
            }
            else if (days is < RetentionSettings.MinDays or > RetentionSettings.MaxDays)
            {
                errors[$"retention.days.{name}"] = [$"Must be between {RetentionSettings.MinDays} and {RetentionSettings.MaxDays}, or null to keep."];
            }
        }
    }

    /// <summary>Applies a validated request.</summary>
    public RetentionSettings Apply(RetentionSettings s)
    {
        var days = new Dictionary<MailType, int>(s.Days);
        foreach (var (name, value) in Days ?? new Dictionary<string, int?>())
        {
            days[SnakeCaseEnumConverter<MailType>.FromDb(name)] = value ?? 0;
        }

        return s with { Enabled = Enabled ?? s.Enabled, Days = days };
    }
}
