using System.Net;

namespace GmailOrganiser.Gmail.Fake;

public sealed partial class FakeGmailClient
{
    /// <summary>Renames as <c>labels.patch</c> does: the id stays, nested labels keep their names; an unknown label is a 404.</summary>
    public async Task<GmailLabel> RenameLabelAsync(string id, string newName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        GmailLimits.EnsureValidLabelName(newName);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        return await RetryAsync(() => labels.Rename(id, newName)
            ?? throw GmailRetryPolicy.CreateApiException(HttpStatusCode.NotFound, "notFound"), ct).ConfigureAwait(false);
    }

    /// <summary>Deletes as <c>labels.delete</c> does, taking the label off every message; an unknown label is already gone.</summary>
    public async Task DeleteLabelAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await RetryAsync(() =>
        {
            DeleteLabel(id);
            return true;
        }, ct).ConfigureAwait(false);
    }
}
