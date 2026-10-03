using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GmailOrganiser.Claude;

public static class ClaudeExtensions
{
    /// <summary>
    /// Registers the Claude review queue with <see cref="NoClaudeReviewStarter"/> as the default starter and the SignalR
    /// item notifier. Needs <c>AddReview</c> and <c>AddJobs</c>.
    /// </summary>
    public static IServiceCollection AddClaude(this IServiceCollection services)
    {
        services.AddScoped<ExternalReviewQuery>();
        services.AddScoped<ExternalReviewService>();
        services.AddSingleton<IClaudeReviewStarter, NoClaudeReviewStarter>();
        services.TryAddSingleton<IExternalReviewNotifier, SignalRExternalReviewNotifier>();
        return services;
    }
}
