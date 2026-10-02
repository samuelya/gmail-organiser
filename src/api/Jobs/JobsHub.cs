using Microsoft.AspNetCore.SignalR;

namespace GmailOrganiser.Jobs;

/// <summary>
/// Server-push hub for job progress. Clients call nothing: on connect they get <see cref="SnapshotEvent"/>
/// with the active jobs, then <see cref="ChangedEvent"/> for every published change.
/// <para>
/// Client contract: the connection receives <see cref="ChangedEvent"/> from before the snapshot is read, so a
/// change can arrive before the snapshot and the snapshot can hold an older state of that job. Merge every
/// <see cref="JobDto"/> (snapshot item or change) by <see cref="JobDto.Id"/>, keeping the one with the later
/// <see cref="JobDto.UpdatedAt"/>; never replace the list wholesale with the snapshot. On reconnect, drop held
/// active jobs that are neither in the new snapshot nor changed on the new connection.
/// </para>
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
