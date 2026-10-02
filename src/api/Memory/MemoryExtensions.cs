using GmailOrganiser.Analysis;

namespace GmailOrganiser.Memory;

public static class MemoryExtensions
{
    /// <summary>Registers decision memory and the memory short-circuit. Needs <c>AddLlm</c> and <c>AddSettings</c>.</summary>
    public static IServiceCollection AddMemory(this IServiceCollection services)
    {
        services.AddScoped<IDecisionMemory, DecisionMemory>();
        services.AddScoped<IAnalysisShortCircuit, MemoryShortCircuit>();
        return services;
    }
}
