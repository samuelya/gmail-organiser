using GmailOrganiser.Analysis;

namespace GmailOrganiser.Memory;

public static class MemoryExtensions
{
    /// <summary>
    /// Registers decision memory, the memory short-circuit and the background decision embedding. Needs <c>AddLlm</c>
    /// and <c>AddSettings</c>.
    /// </summary>
    public static IServiceCollection AddMemory(this IServiceCollection services)
    {
        services.AddScoped<IDecisionMemory, DecisionMemory>();
        services.AddScoped<IAnalysisShortCircuit, MemoryShortCircuit>();
        services.AddSingleton<DecisionEmbeddingQueue>();
        services.AddSingleton<IDecisionEmbeddingQueue>(sp => sp.GetRequiredService<DecisionEmbeddingQueue>());
        services.AddHostedService<DecisionEmbeddingService>();
        return services;
    }
}
