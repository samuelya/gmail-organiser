namespace GmailOrganiser.Review;

public static class ReviewExtensions
{
    /// <summary>Registers review, decisions and the label cache. Needs <c>AddGmail</c> and <c>AddAnalysis</c>.</summary>
    public static IServiceCollection AddReview(this IServiceCollection services)
    {
        services.AddSingleton<LabelCatalog>();
        services.AddScoped<DecisionRecorder>();
        services.AddScoped<ReviewQuery>();
        services.AddScoped<ReviewService>();
        return services;
    }
}
