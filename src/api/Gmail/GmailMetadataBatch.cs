using System.Net;
using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Requests;
using Google.Apis.Util;

namespace GmailOrganiser.Gmail;

/// <summary>
/// One Gmail batch call of <c>messages.get</c> (<c>format=metadata</c>, or <c>format=minimal</c> for labels only) or
/// of <c>labels.get</c>, classified for <see cref="GmailRetryPolicy"/>.
/// </summary>
public static class GmailMetadataBatch
{
    private const string Me = "me";

    /// <summary>
    /// Sends <paramref name="ids"/> as one batch: 404s are dropped (deleted meanwhile), rate-limited and transient 5xx
    /// items are returned for retry, any other item error throws. The whole batch goes out as one burst, so its full
    /// quota cost is spent just before it is sent.
    /// </summary>
    public static Task<GmailBatchAttempt<string, GmailMessageMetadata>> SendAsync(
        GmailService service, IReadOnlyList<string> ids, GmailQuotaLimiter quota, ILogger logger, CancellationToken ct) =>
        SendAsync(service, ids, request =>
        {
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
            request.MetadataHeaders = new Repeatable<string>(GmailMetadataMapper.MetadataHeaders);
        }, GmailMetadataMapper.Map, quota, logger, ct);

    /// <summary>
    /// As <see cref="SendAsync(GmailService, IReadOnlyList{string}, GmailQuotaLimiter, ILogger, CancellationToken)"/>
    /// with <c>format=minimal</c>: ids and label ids only. Costs the same quota per item; the saving is payload.
    /// </summary>
    public static Task<GmailBatchAttempt<string, GmailMessageLabels>> SendLabelsAsync(
        GmailService service, IReadOnlyList<string> ids, GmailQuotaLimiter quota, ILogger logger, CancellationToken ct) =>
        SendAsync(service, ids, request => request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Minimal,
            message => new GmailMessageLabels(message.Id, [.. message.LabelIds ?? []]), quota, logger, ct);

    /// <summary>As the message sends, with <c>labels.get</c>: one <see cref="GmailLabelTotal"/> per label still in Gmail.</summary>
    public static Task<GmailBatchAttempt<string, GmailLabelTotal>> SendLabelTotalsAsync(
        GmailService service, IReadOnlyList<string> ids, GmailQuotaLimiter quota, ILogger logger, CancellationToken ct) =>
        SendAsync<Label, GmailLabelTotal>(service, ids, id => service.Users.Labels.Get(Me, id),
            label => new GmailLabelTotal(label.Id, label.MessagesTotal ?? 0), GmailQuotaLimiter.LabelCallUnits, quota, logger, ct);

    private static Task<GmailBatchAttempt<string, T>> SendAsync<T>(
        GmailService service, IReadOnlyList<string> ids, Action<UsersResource.MessagesResource.GetRequest> configure,
        Func<Message, T> map, GmailQuotaLimiter quota, ILogger logger, CancellationToken ct) =>
        SendAsync<Message, T>(service, ids, id =>
        {
            var request = service.Users.Messages.Get(Me, id);
            configure(request);
            return request;
        }, map, GmailQuotaLimiter.MessageCallUnits, quota, logger, ct);

    private static async Task<GmailBatchAttempt<string, T>> SendAsync<TResponse, T>(
        GmailService service, IReadOnlyList<string> ids, Func<string, IClientServiceRequest> request, Func<TResponse, T> map,
        int unitsPerItem, GmailQuotaLimiter quota, ILogger logger, CancellationToken ct)
        where TResponse : class
    {
        var succeeded = new List<T>(ids.Count);
        var retry = new List<string>();
        var answered = new HashSet<string>(StringComparer.Ordinal);
        GoogleApiException? failure = null;
        var batch = new BatchRequest(service);
        foreach (var id in ids)
        {
            batch.Queue<TResponse>(request(id), (message, error, _, response) =>
            {
                answered.Add(id);
                if (error is null)
                {
                    succeeded.Add(map(message));
                }
                else if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    logger.LogDebug("Gmail item {Id} no longer exists", id);
                }
                else if (GmailRetryPolicy.IsRetryable(response.StatusCode, error))
                {
                    retry.Add(id);
                }
                else
                {
                    failure ??= new GoogleApiException(service.Name, error.Message) { HttpStatusCode = response.StatusCode, Error = error };
                }
            });
        }

        await quota.AcquireAsync(ids.Count * unitsPerItem, ct);
        try
        {
            await batch.ExecuteAsync(ct);
        }
        catch (Exception ex) when (AsApiException(ex) is { } api && GmailRetryPolicy.IsRetryable(api.HttpStatusCode, api.Error))
        {
            // The batch call itself was throttled or failed (or an item's 5xx had no parsable error body):
            // retry every item without an answer.
            logger.LogDebug("Gmail batch call failed with {Status}; retrying unanswered items", (int)api.HttpStatusCode);
            retry.AddRange(ids.Where(id => !answered.Contains(id)));
        }
        catch (HttpRequestException ex) when (ex.InnerException is GoogleApiException api)
        {
            // Surface the status (e.g. 401) as single requests do, so the caller can flag reauth.
            throw new GoogleApiException(service.Name, api.Message, ex) { HttpStatusCode = api.HttpStatusCode, Error = api.Error };
        }

        if (failure is not null)
        {
            throw failure;
        }

        if (retry.Count > 0)
        {
            logger.LogDebug("Gmail batch: {Count} of {Total} items to retry", retry.Count, ids.Count);
        }

        return new GmailBatchAttempt<string, T>(succeeded, retry);
    }

    /// <summary>
    /// The API error behind a failed batch. <c>BatchRequest.ExecuteAsync</c> (Google.Apis 1.77) reports a non-2xx
    /// batch response as an <see cref="HttpRequestException"/> without a status code, wrapping a
    /// <see cref="GoogleApiException"/> that has it; an item error it cannot parse escapes as a bare
    /// <see cref="GoogleApiException"/>.
    /// </summary>
    public static GoogleApiException? AsApiException(Exception ex) =>
        ex as GoogleApiException ?? (ex as HttpRequestException)?.InnerException as GoogleApiException;
}
