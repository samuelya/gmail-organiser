namespace GmailOrganiser.Common;

public static class LoggingExtensions
{
    /// <summary>
    /// Uses the built-in JSON console logger only.
    /// Rule: never log email content (subjects, bodies, snippets, addresses), OAuth tokens,
    /// API keys or any other secret. Log identifiers and counts instead.
    /// </summary>
    public static WebApplicationBuilder AddJsonConsoleLogging(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
        });
        return builder;
    }
}
