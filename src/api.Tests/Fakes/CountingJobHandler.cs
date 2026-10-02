using System.Collections.Concurrent;
using GmailOrganiser.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GmailOrganiser.Tests.Fakes;

public sealed record CountingCursor(int NextStep);

/// <summary>Shared state of <see cref="CountingJobHandler"/>: what ran, and hooks to act between steps.</summary>
public sealed class CountingJobState
{
    public int Steps { get; set; } = 5;
    public int? FailAtStep { get; set; }
    public ConcurrentQueue<int> Executed { get; } = new();

    /// <summary>Runs after a step's work and before its checkpoint.</summary>
    public Func<Guid, int, Task>? AfterStep { get; set; }

    /// <summary>Runs after a checkpoint returned pause or cancel, just before the handler returns.</summary>
    public Func<Guid, Task>? OnStop { get; set; }

    /// <summary>Return normally (instead of throwing) when the token is cancelled, as a polite handler may.</summary>
    public bool ReturnOnShutdown { get; set; }
}

/// <summary>Test-only handler: runs steps 1..N, checkpointing after each, resuming from the cursor.</summary>
public sealed class CountingJobHandler(CountingJobState state) : IJobHandler
{
    public const string JobType = "test-counting";

    public string Type => JobType;

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var next = ctx.ReadCursor<CountingCursor>()?.NextStep ?? 1;
        for (var step = next; step <= state.Steps; step++)
        {
            if (step == state.FailAtStep)
            {
                throw new InvalidOperationException($"Synthetic failure at step {step}");
            }

            state.Executed.Enqueue(step);
            if (state.AfterStep is { } hook)
            {
                await hook(ctx.JobId, step);
            }

            if (state.ReturnOnShutdown && ct.IsCancellationRequested)
            {
                return;
            }

            var signal = await ctx.CheckpointAsync(new CountingCursor(step + 1), new JobProgress(step, state.Steps, $"step {step}"), ct);
            if (signal != JobSignal.Continue)
            {
                if (state.OnStop is { } onStop)
                {
                    await onStop(ctx.JobId);
                }

                return;
            }
        }
    }

    /// <summary>
    /// A host with the counting handler registered. Without <paramref name="pollInterval"/> the hosted
    /// runner is removed so tests drive <see cref="JobRunner"/> directly.
    /// </summary>
    public static WebApplicationFactory<Program> CreateHost(ApiFactory factory, CountingJobState state, TimeSpan? pollInterval = null) =>
        factory.WithWebHostBuilder(b =>
        {
            if (pollInterval is { } interval)
            {
                b.UseSetting("Jobs:PollInterval", interval.ToString("c"));
            }

            b.ConfigureTestServices(services =>
            {
                services.AddSingleton(state);
                services.AddKeyedScoped<IJobHandler, CountingJobHandler>(JobType);
                if (pollInterval is null)
                {
                    var runner = services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobRunner));
                    services.Remove(runner);
                }
            });
        });
}
