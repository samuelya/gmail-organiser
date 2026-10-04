using System.Net;
using Google;
using Google.Apis.Gmail.v1.Data;

namespace GmailOrganiser.Gmail;

public sealed partial class GoogleGmailClient
{
    public Task<GmailLabel> RenameLabelAsync(string id, string newName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        GmailLimits.EnsureValidLabelName(newName);
        return RunAsync(async service =>
        {
            try
            {
                return await retry.ExecuteAsync(async token =>
                {
                    // labels.patch and labels.delete cost what labels.create costs.
                    await quota.AcquireAsync(GmailQuotaLimiter.LabelCreateUnits, token);
                    var patched = await service.Users.Labels.Patch(new Label { Name = newName }, Me, id).ExecuteAsync(token);
                    logger.LogInformation("Renamed a Gmail label");
                    return ToLabel(patched);
                }, ct);
            }
            catch (GoogleApiException ex) when (IsNameClash(ex))
            {
                throw new GmailLabelExistsException("Another label already has this name.", ex);
            }
        }, ct);
    }

    public Task DeleteLabelAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return RunAsync(service => retry.ExecuteAsync(async token =>
        {
            await quota.AcquireAsync(GmailQuotaLimiter.LabelCreateUnits, token);
            try
            {
                await service.Users.Labels.Delete(Me, id).ExecuteAsync(token);
                logger.LogInformation("Deleted a Gmail label");
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                logger.LogDebug("Gmail no longer has the label to delete");
            }

            return true;
        }, ct), ct);
    }

    /// <summary>
    /// Gmail answers a create with an existing name with 409; a patch to one answers 409 or a 400 whose message says
    /// the name "exists or conflicts".
    /// </summary>
    public static bool IsNameClash(GoogleApiException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.HttpStatusCode == HttpStatusCode.Conflict
            || (ex.HttpStatusCode == HttpStatusCode.BadRequest
                && (NamesClash(ex.Error?.Message) || ex.Error?.Errors?.Any(e => NamesClash(e.Message)) == true));
    }

    private static bool NamesClash(string? text) =>
        text is not null && text.Contains("exists or conflicts", StringComparison.OrdinalIgnoreCase);
}
