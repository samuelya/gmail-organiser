namespace GmailOrganiser.Common;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>Origins allowed to send state-changing requests to <c>/api/**</c>, e.g. <c>http://localhost:4200</c>.</summary>
    public string[] AllowedOrigins { get; set; } = [];
}
