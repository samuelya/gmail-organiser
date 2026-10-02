using System.Globalization;
using System.Net;
using GmailOrganiser.Settings;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Requests;
using Google.Apis.Services;
using Google.Apis.Util;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Gmail;

/// <summary>
/// <see cref="IGmailClient"/> over <c>Google.Apis.Gmail.v1</c>. Credentials come from the stored refresh token and
/// the Google client in settings; the SDK refreshes access tokens in memory and never persists them.
/// </summary>
public sealed class GoogleGmailClient(
    ITokenStore tokens,
    GoogleClientService googleClient,
    GmailRetryPolicy retry,
    GmailQuotaLimiter quota,
    IOptions<GmailOptions> options,
    ILogger<GoogleGmailClient> logger) : IGmailClient
{
    private const string Me = "me";

    public Task<GmailProfile> GetProfileAsync(CancellationToken ct) =>
        RunAsync(async service =>
        {
            var profile = await service.Users.GetProfile(Me).ExecuteAsync(ct);
            return new GmailProfile(
                profile.EmailAddress ?? "",
                profile.MessagesTotal ?? 0,
                profile.HistoryId?.ToString(CultureInfo.InvariantCulture) ?? "");
        }, ct);

    public Task<MessageIdPage> ListMessageIdsAsync(MessageListQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.EnsureValid();
        return RunAsync(service => retry.ExecuteAsync(async token =>
        {
            await quota.AcquireAsync(GmailQuotaLimiter.MessageCallUnits, token);
            var request = service.Users.Messages.List(Me);
            request.Q = query.Query;
            request.LabelIds = query.LabelIds is { Count: > 0 } ? new Repeatable<string>(query.LabelIds) : null;
            request.PageToken = query.PageToken;
            request.MaxResults = query.MaxResults;
            request.IncludeSpamTrash = false;
            var response = await request.ExecuteAsync(token);
            var messages = response.Messages?.Select(m => new MessageRef(m.Id, m.ThreadId ?? "")).ToList() ?? [];
            logger.LogDebug("Listed {Count} Gmail message ids", messages.Count);
            return new MessageIdPage(messages, response.NextPageToken, (long?)response.ResultSizeEstimate);
        }, ct), ct);
    }

    public async Task<IReadOnlyList<GmailMessageMetadata>> GetMessagesMetadataAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        var fetched = await RunAsync(async service =>
        {
            var all = new List<GmailMessageMetadata>(ids.Count);
            foreach (var chunk in ids.Distinct(StringComparer.Ordinal).Chunk(options.Value.BatchSize))
            {
                all.AddRange(await retry.ExecuteBatchAsync<string, GmailMessageMetadata>(
                    chunk, (pending, token) => SendMetadataBatchAsync(service, pending, token), ct));
            }

            return all;
        }, ct);

        var byId = fetched.ToDictionary(m => m.Id, StringComparer.Ordinal);
        logger.LogDebug("Fetched metadata for {Fetched} of {Requested} Gmail messages", byId.Count, ids.Count);
        return [.. ids.Distinct(StringComparer.Ordinal).Where(byId.ContainsKey).Select(id => byId[id])];
    }

    /// <summary>One Gmail batch call: 404s are dropped (deleted meanwhile), rate-limited items are returned for retry.</summary>
    private async Task<GmailBatchAttempt<string, GmailMessageMetadata>> SendMetadataBatchAsync(
        GmailService service, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var succeeded = new List<GmailMessageMetadata>(ids.Count);
        var rateLimited = new List<string>();
        GoogleApiException? failure = null;
        var batch = new BatchRequest(service);
        foreach (var id in ids)
        {
            await quota.AcquireAsync(GmailQuotaLimiter.MessageCallUnits, ct);
            var request = service.Users.Messages.Get(Me, id);
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
            request.MetadataHeaders = new Repeatable<string>(GmailMetadataMapper.MetadataHeaders);
            batch.Queue<Message>(request, (message, error, _, response) =>
            {
                if (error is null)
                {
                    succeeded.Add(GmailMetadataMapper.Map(message));
                }
                else if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    logger.LogDebug("Gmail message {MessageId} no longer exists", id);
                }
                else if (GmailRetryPolicy.IsRateLimited(response.StatusCode, error))
                {
                    rateLimited.Add(id);
                }
                else
                {
                    failure ??= new GoogleApiException(service.Name, error.Message) { HttpStatusCode = response.StatusCode, Error = error };
                }
            });
        }

        try
        {
            await batch.ExecuteAsync(ct);
        }
        catch (GoogleApiException ex) when (GmailRetryPolicy.IsRateLimited(ex))
        {
            // The batch call itself was throttled: nothing in it was processed.
            return new GmailBatchAttempt<string, GmailMessageMetadata>([], ids);
        }

        if (failure is not null)
        {
            throw failure;
        }

        if (rateLimited.Count > 0)
        {
            logger.LogDebug("Gmail rate-limited {Count} of {Total} batch items", rateLimited.Count, ids.Count);
        }

        return new GmailBatchAttempt<string, GmailMessageMetadata>(succeeded, rateLimited);
    }

    /// <summary>Runs <paramref name="call"/> with a fresh service; a revoked or expired grant flags reauth.</summary>
    private async Task<T> RunAsync<T>(Func<GmailService, Task<T>> call, CancellationToken ct)
    {
        var (flow, service) = await CreateServiceAsync(ct);
        using (flow)
        using (service)
        {
            try
            {
                return await call(service);
            }
            catch (TokenResponseException ex) when (ex.Error?.Error == "invalid_grant")
            {
                // Revoked, or expired (a consent screen in Testing mode issues 7-day refresh tokens).
                await tokens.MarkReauthRequiredAsync(CancellationToken.None);
                throw new GmailNotConnectedException("The Gmail connection was revoked or has expired; reconnect Gmail.", ex);
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.Unauthorized)
            {
                await tokens.MarkReauthRequiredAsync(CancellationToken.None);
                throw new GmailNotConnectedException("Gmail rejected the stored credentials; reconnect Gmail.", ex);
            }
        }
    }

    private async Task<(GoogleAuthorizationCodeFlow Flow, GmailService Service)> CreateServiceAsync(CancellationToken ct)
    {
        var token = await tokens.GetAsync(ct)
            ?? throw new GmailNotConnectedException("Gmail is not connected.");
        if (token.ReauthRequired)
        {
            throw new GmailNotConnectedException("The Gmail connection was revoked or has expired; reconnect Gmail.");
        }

        var client = await googleClient.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(client.ClientId) || string.IsNullOrWhiteSpace(client.ClientSecret))
        {
            throw new GmailNotConnectedException("The Google OAuth client ID and secret are not configured.");
        }

        // No DataStore: the flow keeps the access token in memory only.
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = client.ClientId, ClientSecret = client.ClientSecret },
            Scopes = GmailScopes.All,
        });
        var credential = new UserCredential(flow, Me, new TokenResponse { RefreshToken = token.RefreshToken });
        var service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = SettingsEndpoints.ApplicationName,
        });
        return (flow, service);
    }
}
