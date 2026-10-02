using GmailOrganiser.Jobs;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class JobRunnerTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly CountingJobState state = new();
    private WebApplicationFactory<Program> host = null!;
    private JobRunner runner = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Jobs.ExecuteDeleteAsync();
        }

        host = CountingJobHandler.CreateHost(factory, state);
        runner = ActivatorUtilities.CreateInstance<JobRunner>(host.Services);
    }

    public async ValueTask DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public async Task Job_completes_with_final_progress()
    {
        var job = await EnqueueAsync();

        (await RunNextAsync()).ShouldBe(job.Id);

        var done = await GetAsync(job.Id);
        done.Status.ShouldBe("completed");
        done.Progress.ShouldBe(new JobProgress(5, 5, "step 5"));
        done.StartedAt.ShouldNotBeNull();
        done.FinishedAt.ShouldNotBeNull();
        done.Error.ShouldBeNull();
        state.Executed.ShouldBe([1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task Enqueue_returns_the_active_job_of_the_same_type()
    {
        var first = await EnqueueAsync();
        var second = await EnqueueAsync();

        second.Id.ShouldBe(first.Id);
        await using var db = postgres.CreateDbContext();
        (await db.Jobs.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Pause_then_resume_continues_from_the_cursor_without_repeating_steps()
    {
        var job = await EnqueueAsync();
        state.AfterStep = (id, step) => step == 2 ? WithJobsAsync(s => s.PauseAsync(id, Ct)) : Task.CompletedTask;

        await RunNextAsync();

        (await GetAsync(job.Id)).Status.ShouldBe("paused");
        (await ReadCursorAsync(job.Id)).ShouldBe(new CountingCursor(3));
        state.Executed.ShouldBe([1, 2]);

        state.AfterStep = null;
        (await WithJobsAsync(s => s.ResumeAsync(job.Id, Ct))).ShouldBe(JobActionResult.Ok);
        (await GetAsync(job.Id)).Status.ShouldBe("queued");
        await RunNextAsync();

        (await GetAsync(job.Id)).Status.ShouldBe("completed");
        state.Executed.ShouldBe([1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task Cancel_while_running_ends_cancelled()
    {
        var job = await EnqueueAsync();
        state.AfterStep = (id, step) => step == 2 ? WithJobsAsync(s => s.CancelAsync(id, Ct)) : Task.CompletedTask;

        await RunNextAsync();

        var cancelled = await GetAsync(job.Id);
        cancelled.Status.ShouldBe("cancelled");
        cancelled.FinishedAt.ShouldNotBeNull();
        state.Executed.ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Cancel_of_a_queued_job_is_immediate_and_final()
    {
        var job = await EnqueueAsync();

        (await WithJobsAsync(s => s.CancelAsync(job.Id, Ct))).ShouldBe(JobActionResult.Ok);

        (await GetAsync(job.Id)).Status.ShouldBe("cancelled");
        (await runner.ClaimAsync(Ct)).ShouldBeEmpty();
        (await WithJobsAsync(s => s.ResumeAsync(job.Id, Ct))).ShouldBe(JobActionResult.Conflict);
    }

    [Fact]
    public async Task Startup_recovery_requeues_a_running_job_which_resumes_from_its_cursor()
    {
        var job = await EnqueueAsync(new CountingCursor(4));
        await using (var db = postgres.CreateDbContext())
        {
            await db.Jobs.Where(j => j.Id == job.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Running), Ct);
        }

        (await runner.RecoverAsync(Ct)).ShouldBe(1);
        (await GetAsync(job.Id)).Status.ShouldBe("queued");

        await RunNextAsync();

        (await GetAsync(job.Id)).Status.ShouldBe("completed");
        state.Executed.ShouldBe([4, 5]);
    }

    [Fact]
    public async Task Host_shutdown_leaves_the_job_running_with_its_last_cursor()
    {
        var job = await EnqueueAsync();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        state.AfterStep = (_, step) =>
        {
            if (step == 3)
            {
                shutdown.Cancel();
            }

            return Task.CompletedTask;
        };

        var claimed = await runner.ClaimAsync(Ct);
        await runner.RunAsync(claimed.Single(), shutdown.Token);

        (await GetAsync(job.Id)).Status.ShouldBe("running");
        (await ReadCursorAsync(job.Id)).ShouldBe(new CountingCursor(3));
    }

    [Fact]
    public async Task Concurrent_claims_take_the_job_once()
    {
        var job = await EnqueueAsync();
        var other = ActivatorUtilities.CreateInstance<JobRunner>(host.Services);

        var results = await Task.WhenAll(runner.ClaimAsync(Ct), other.ClaimAsync(Ct), runner.ClaimAsync(Ct));

        results.SelectMany(r => r).ShouldBe([job.Id]);
    }

    [Fact]
    public async Task One_job_runs_per_queue()
    {
        var running = await EnqueueAsync();
        (await runner.ClaimAsync(Ct)).ShouldBe([running.Id]);
        await WithJobsAsync(s => s.EnqueueAsync("test-other", JobQueues.Fetch, null, Ct));

        (await runner.ClaimAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_type_fails_and_the_runner_keeps_going()
    {
        var unknown = await WithJobsAsync(s => s.EnqueueAsync("test-unknown", JobQueues.Fetch, null, Ct));
        var known = await EnqueueAsync();

        await RunNextAsync();
        await RunNextAsync();

        var failed = await GetAsync(unknown.Id);
        failed.Status.ShouldBe("failed");
        failed.Error.ShouldNotBeNull().ShouldContain("test-unknown");
        (await GetAsync(known.Id)).Status.ShouldBe("completed");
    }

    [Fact]
    public async Task Handler_exception_fails_the_job_keeps_the_cursor_and_resume_finishes()
    {
        var job = await EnqueueAsync();
        state.FailAtStep = 3;

        await RunNextAsync();

        var failed = await GetAsync(job.Id);
        failed.Status.ShouldBe("failed");
        failed.Error.ShouldBe("Synthetic failure at step 3");
        (await ReadCursorAsync(job.Id)).ShouldBe(new CountingCursor(3));

        state.FailAtStep = null;
        (await WithJobsAsync(s => s.ResumeAsync(job.Id, Ct))).ShouldBe(JobActionResult.Ok);
        await RunNextAsync();

        var done = await GetAsync(job.Id);
        done.Status.ShouldBe("completed");
        done.Error.ShouldBeNull();
        state.Executed.ShouldBe([1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task Hosted_runner_picks_up_and_completes_a_job()
    {
        await using var live = CountingJobHandler.CreateHost(factory, state, TimeSpan.FromMilliseconds(50));
        _ = live.Services; // starts the host and its runner
        var job = await EnqueueAsync();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((await GetAsync(job.Id)).Status != "completed" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        (await GetAsync(job.Id)).Status.ShouldBe("completed");
    }

    private Task<JobDto> EnqueueAsync(CountingCursor? cursor = null) =>
        WithJobsAsync(s => s.EnqueueAsync(CountingJobHandler.JobType, JobQueues.Fetch, cursor, Ct));

    private async Task<JobDto> GetAsync(Guid id) => (await WithJobsAsync(s => s.GetAsync(id, Ct))).ShouldNotBeNull();

    private async Task<T> WithJobsAsync<T>(Func<IJobService, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IJobService>());
    }

    private async Task<Guid> RunNextAsync()
    {
        var id = (await runner.ClaimAsync(Ct)).ShouldHaveSingleItem();
        await runner.RunAsync(id, Ct);
        return id;
    }

    private async Task<CountingCursor?> ReadCursorAsync(Guid id)
    {
        await using var db = postgres.CreateDbContext();
        var json = await db.Jobs.Where(j => j.Id == id).Select(j => j.Cursor).SingleAsync(Ct);
        return json is null ? null : System.Text.Json.JsonSerializer.Deserialize<CountingCursor>(json, System.Text.Json.JsonSerializerOptions.Web);
    }
}
