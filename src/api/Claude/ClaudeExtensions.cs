namespace GmailOrganiser.Claude;

public static class ClaudeExtensions
{
    /// <summary>Registers the Claude review queue with <see cref="NoClaudeReviewStarter"/> as the default starter. Needs <c>AddReview</c>.</summary>
    public static IServiceCollection AddClaude(this IServiceCollection services)
    {
        services.AddScoped<ExternalReviewQuery>();
        services.AddScoped<ExternalReviewService>();
        services.AddSingleton<IClaudeReviewStarter, NoClaudeReviewStarter>();
        return services;
    }
}
