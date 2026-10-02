using Microsoft.AspNetCore.SignalR;

namespace GmailOrganiser.Jobs;

/// <summary>
/// Server-push hub for job progress. Clients call nothing: on connect they get <see cref="SnapshotEvent"/>
/// with the active jobs, then <see cref="ChangedEvent"/> for every published change.
/// </summary>
public sealed class JobsHub(IJobService jobs) : Hub
{
    public const string Path = "/hubs/jobs";
    public const string SnapshotEvent = "jobsSnapshot";
    public const string ChangedEvent = "jobChanged";

    public override async Task OnConnectedAsync()
    {
        var active = await jobs.ListAsync(activeOnly: true, Context.ConnectionAborted);
        await Clients.Caller.SendAsync(SnapshotEvent, active, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }
}
