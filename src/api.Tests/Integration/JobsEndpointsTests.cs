using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Jobs;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class JobsEndpointsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private WebApplicationFactory<Program> host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Jobs.ExecuteDeleteAsync();
        }

        host = CountingJobHandler.CreateHost(factory, new CountingJobState());
    }

    public async ValueTask DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public async Task Get_returns_the_job_and_404_problem_for_an_unknown_id()
    {
        var job = await EnqueueAsync(CountingJobHandler.JobType);
        var client = host.CreateClient();

        var dto = await client.GetFromJsonAsync<JobDto>($"/api/jobs/{job.Id}", Ct);
        dto.ShouldNotBeNull().Status.ShouldBe("queued");
        dto.Queue.ShouldBe(JobQueues.Fetch);

        var missing = await client.GetAsync($"/api/jobs/{Guid.NewGuid()}", Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task List_returns_active_jobs_and_the_last_finished_ones()
    {
        var active = await EnqueueAsync("test-active");
        await using (var db = postgres.CreateDbContext())
        {
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < IJobService.RecentFinishedCount + 5; i++)
            {
                db.Jobs.Add(new JobRow
                {
                    Id = Guid.NewGuid(),
                    Type = $"test-done-{i}",
                    Queue = JobQueues.Fetch,
                    Status = JobStatus.Completed,
                    CreatedAt = now,
                    UpdatedAt = now,
                    FinishedAt = now.AddMinutes(i),
                });
            }

            await db.SaveChangesAsync(Ct);
        }

        var client = host.CreateClient();
        var all = await client.GetFromJsonAsync<List<JobDto>>("/api/jobs", Ct);
        var activeOnly = await client.GetFromJsonAsync<List<JobDto>>("/api/jobs?activeOnly=true", Ct);

        all.ShouldNotBeNull().Count.ShouldBe(1 + IJobService.RecentFinishedCount);
        all[0].Id.ShouldBe(active.Id);
        all[1].Type.ShouldBe($"test-done-{IJobService.RecentFinishedCount + 4}");
        activeOnly.ShouldNotBeNull().Select(j => j.Id).ShouldBe([active.Id]);
    }

    [Fact]
    public async Task Pause_resume_cancel_return_204_and_change_the_status()
    {
        var job = await EnqueueAsync(CountingJobHandler.JobType);

        (await PostAsync(job.Id, "pause")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetStatusAsync(job.Id)).ShouldBe("paused");
        (await PostAsync(job.Id, "resume")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetStatusAsync(job.Id)).ShouldBe("queued");
        (await PostAsync(job.Id, "cancel")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetStatusAsync(job.Id)).ShouldBe("cancelled");
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("cancel")]
    public async Task Transition_from_a_finished_job_is_409_problem(string action)
    {
        var job = await EnqueueAsync(CountingJobHandler.JobType);
        await using (var db = postgres.CreateDbContext())
        {
            await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Completed), Ct);
        }

        var response = await PostAsync(job.Id, action);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct)).ShouldNotBeNull().Status.ShouldBe(409);
        (await GetStatusAsync(job.Id)).ShouldBe("completed");
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("cancel")]
    public async Task Transition_of_an_unknown_job_is_404(string action)
    {
        (await PostAsync(Guid.NewGuid(), action)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<JobDto> EnqueueAsync(string type)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IJobService>().EnqueueAsync(type, JobQueues.Fetch, null, Ct)).Job;
    }

    private async Task<string> GetStatusAsync(Guid id) =>
        (await host.CreateClient().GetFromJsonAsync<JobDto>($"/api/jobs/{id}", Ct)).ShouldNotBeNull().Status;

    private Task<HttpResponseMessage> PostAsync(Guid id, string action)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client.PostAsync($"/api/jobs/{id}/{action}", null, Ct);
    }
}
