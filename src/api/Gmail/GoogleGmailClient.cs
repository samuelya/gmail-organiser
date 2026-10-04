using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using GmailOrganiser.Settings;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Gmail;

/// <summary>
/// <see cref="IGmailClient"/> over <c>Google.Apis.Gmail.v1</c>. Credentials come from the stored refresh token and
/// the Google client in settings; the SDK refreshes access tokens in memory and never persists them.
/// </summary>
public sealed partial class GoogleGmailClient(
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
        return RunAsync(async service =>
        {
            try
            {
                return await ListAsync(service, query, ct);
            }
            catch (GoogleApiException ex) when (query.PageToken is not null && IsInvalidPageToken(ex))
            {
                throw new GmailInvalidPageTokenException("Gmail rejected the list page token.", ex);
            }
        }, ct);
    }

    /// <summary>
    /// Gmail answers an expired or malformed page token with a 400 whose message names the page token. Other 400s
    /// (a bad query or label id) are real validation errors and must surface as they are.
    /// </summary>
    public static bool IsInvalidPageToken(GoogleApiException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.HttpStatusCode == HttpStatusCode.BadRequest
            && (NamesPageToken(ex.Error?.Message)
                || ex.Error?.Errors?.Any(e => NamesPageToken(e.Message) || NamesPageToken(e.Location)) == true);
    }

    private static bool NamesPageToken(string? text) =>
        text is not null
        && (text.Contains("pageToken", StringComparison.OrdinalIgnoreCase) || text.Contains("page token", StringComparison.OrdinalIgnoreCase));

    private Task<MessageIdPage> ListAsync(GmailService service, MessageListQuery query, CancellationToken ct) =>
        retry.ExecuteAsync(async token =>
        {
            await quota.AcquireAsync(GmailQuotaLimiter.MessageCallUnits, token);
            var request = service.Users.Messages.List(Me);
            request.Q = query.Query;
            request.LabelIds = query.LabelIds is { Count: > 0 } ? new Repeatable<string>(query.LabelIds) : null;
            request.PageToken = query.PageToken;
            request.MaxResults = query.MaxResults;
            request.IncludeSpamTrash = query.IncludeSpamTrash;
            var response = await request.ExecuteAsync(token);
            var messages = response.Messages?.Select(m => new MessageRef(m.Id, m.ThreadId ?? "")).ToList() ?? [];
            logger.LogDebug("Listed {Count} Gmail message ids", messages.Count);
            return new MessageIdPage(messages, response.NextPageToken, (long?)response.ResultSizeEstimate);
        }, ct);

    public async Task<IReadOnlyList<GmailMessageMetadata>> GetMessagesMetadataAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var fetched = await GetInBatchesAsync(ids, GmailMetadataBatch.SendAsync, m => m.Id, ct);
        logger.LogDebug("Fetched metadata for {Fetched} of {Requested} Gmail messages", fetched.Count, ids.Count);
        return fetched;
    }

    public async Task<IReadOnlyList<GmailMessageLabels>> GetMessagesLabelsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var fetched = await GetInBatchesAsync(ids, GmailMetadataBatch.SendLabelsAsync, m => m.Id, ct);
        logger.LogDebug("Fetched labels for {Fetched} of {Requested} Gmail messages", fetched.Count, ids.Count);
        return fetched;
    }

    /// <summary>Sends the distinct <paramref name="ids"/> in batches of <see cref="GmailOptions.BatchSize"/>; results in request order.</summary>
    private async Task<IReadOnlyList<T>> GetInBatchesAsync<T>(
        IReadOnlyList<string> ids,
        Func<GmailService, IReadOnlyList<string>, GmailQuotaLimiter, ILogger, CancellationToken, Task<GmailBatchAttempt<string, T>>> send,
        Func<T, string> idOf,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        var unique = ids.Distinct(StringComparer.Ordinal).ToList();
        var fetched = await RunAsync(async service =>
        {
            var all = new List<T>(unique.Count);
            foreach (var chunk in unique.Chunk(options.Value.BatchSize))
            {
                all.AddRange(await retry.ExecuteBatchAsync<string, T>(
                    chunk, (pending, token) => send(service, pending, quota, logger, token), ct));
            }

            return all;
        }, ct);

        var byId = fetched.ToDictionary(idOf, StringComparer.Ordinal);
        return [.. unique.Where(byId.ContainsKey).Select(id => byId[id])];
    }

    public Task<long> GetLabelMessagesTotalAsync(string labelId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labelId);
        return RunAsync(service => retry.ExecuteAsync(async token =>
        {
            await quota.AcquireAsync(GmailQuotaLimiter.LabelCallUnits, token);
            var label = await service.Users.Labels.Get(Me, labelId).ExecuteAsync(token);
            return (long)(label.MessagesTotal ?? 0);
        }, ct), ct);
    }

    public async Task<IReadOnlyList<GmailLabelTotal>> GetLabelsMessagesTotalAsync(IReadOnlyList<string> labelIds, CancellationToken ct)
    {
        var fetched = await GetInBatchesAsync(labelIds, GmailMetadataBatch.SendLabelTotalsAsync, l => l.Id, ct);
        logger.LogDebug("Fetched totals for {Fetched} of {Requested} Gmail labels", fetched.Count, labelIds.Count);
        return fetched;
    }

    public Task<HistoryPage> ListHistoryAsync(string startHistoryId, string? pageToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startHistoryId);
        return RunAsync(service => retry.ExecuteAsync(async token =>
        {
            await quota.AcquireAsync(GmailQuotaLimiter.HistoryCallUnits, token);
            var page = await GmailHistoryList.SendAsync(service, startHistoryId, pageToken, token);
            logger.LogDebug("Listed {Count} Gmail history records", page.Records.Count);
            return page;
        }, ct), ct);
    }

    public Task<GmailMessageBody?> GetMessageBodyAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return RunAsync(service => retry.ExecuteAsync(async token =>
        {
            await quota.AcquireAsync(GmailQuotaLimiter.MessageCallUnits, token);
            return await GetBodyAsync(service, id, logger, token);
        }, ct), ct);
    }

    /// <summary>One <c>messages.get</c> (format=full); null on 404. Logs sizes only, never content.</summary>
    public static async Task<GmailMessageBody?> GetBodyAsync(GmailService service, string id, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(logger);
        var request = service.Users.Messages.Get(Me, id);
        request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
        try
        {
            var body = ReadBody(await request.ExecuteAsync(ct));
            logger.LogDebug(
                "Read a Gmail message body: {TextLength} text and {HtmlLength} html characters",
                body.Text?.Length ?? 0,
                body.Html?.Length ?? 0);
            return body;
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            logger.LogDebug("Gmail no longer has the message whose body was requested");
            return null;
        }
    }

    /// <summary>
    /// The first <c>text/plain</c> and first <c>text/html</c> part, depth-first through <c>payload.parts</c>. Parts with
    /// a filename are attachments and skipped with their children, as are attached messages (<c>message/rfc822</c>);
    /// bodies Gmail moved to an attachment id are not fetched. A part that doesn't decode counts as absent.
    /// </summary>
    public static GmailMessageBody ReadBody(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string? text = null;
        string? html = null;
        var stack = new Stack<MessagePart>();
        if (message.Payload is not null)
        {
            stack.Push(message.Payload);
        }

        while (stack.Count > 0 && (text is null || html is null))
        {
            var part = stack.Pop();
            if (!string.IsNullOrEmpty(part.Filename) || IsMimeType(part, "message/rfc822"))
            {
                continue;
            }

            if (part.Parts is { Count: > 0 } children)
            {
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }

                continue;
            }

            if (part.Body?.Data is not { } data)
            {
                continue;
            }

            if (text is null && IsMimeType(part, "text/plain"))
            {
                text = TryDecode(part, data);
            }
            else if (html is null && IsMimeType(part, "text/html"))
            {
                html = TryDecode(part, data);
            }
        }

        return new GmailMessageBody(text, html);
    }

    private static bool IsMimeType(MessagePart part, string mimeType) =>
        string.Equals(part.MimeType, mimeType, StringComparison.OrdinalIgnoreCase);

    private static string? TryDecode(MessagePart part, string data)
    {
        try
        {
            return CharsetOf(part).GetString(Base64Url.DecodeFromChars(data));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The part's <c>Content-Type</c> charset when .NET knows it, UTF-8 otherwise.</summary>
    private static Encoding CharsetOf(MessagePart part)
    {
        var contentType = part.Headers?.FirstOrDefault(h => string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
        if (contentType is null || !MediaTypeHeaderValue.TryParse(contentType, out var parsed) || parsed.CharSet is not { Length: > 0 } charset)
        {
            return Encoding.UTF8;
        }

        charset = charset.Trim('"');
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(charset) ?? Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    public Task<GmailThreadSummary?> GetThreadSummaryAsync(string threadId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return RunAsync(service => retry.ExecuteAsync(async token =>
        {
            await quota.AcquireAsync(GmailQuotaLimiter.ThreadCallUnits, token);
            var request = service.Users.Threads.Get(Me, threadId);
            request.Format = UsersResource.ThreadsResource.GetRequest.FormatEnum.Minimal;
            try
            {
                var thread = await request.ExecuteAsync(token);
                var messages = thread.Messages?
                    .Select(m => new GmailThreadMessage(m.Id, [.. m.LabelIds ?? []]))
                    .ToList() ?? [];
                logger.LogDebug("Read a Gmail thread of {Count} messages", messages.Count);
                return new GmailThreadSummary(thread.Id ?? threadId, messages);
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                logger.LogDebug("Gmail no longer has the thread that was requested");
                return (GmailThreadSummary?)null;
            }
        }, ct), ct);
    }

    public Task<IReadOnlyList<GmailLabel>> ListLabelsAsync(CancellationToken ct) =>
        RunAsync(service => ListLabelsAsync(service, ct), ct);

    private Task<IReadOnlyList<GmailLabel>> ListLabelsAsync(GmailService service, CancellationToken ct) =>
        retry.ExecuteAsync<IReadOnlyList<GmailLabel>>(async token =>
        {
            await quota.AcquireAsync(GmailQuotaLimiter.LabelCallUnits, token);
            var response = await service.Users.Labels.List(Me).ExecuteAsync(token);
            return response.Labels?.Select(ToLabel).ToList() ?? [];
        }, ct);

    public Task<GmailLabel> CreateLabelAsync(string name, CancellationToken ct)
    {
        GmailLimits.EnsureValidLabelName(name);
        return RunAsync(async service =>
        {
            try
            {
                return await retry.ExecuteAsync(async token =>
                {
                    await quota.AcquireAsync(GmailQuotaLimiter.LabelCreateUnits, token);
                    var label = new Label { Name = name, LabelListVisibility = "labelShow", MessageListVisibility = "show" };
                    return ToLabel(await service.Users.Labels.Create(label, Me).ExecuteAsync(token));
                }, ct);
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.Conflict)
            {
                // The name exists (created earlier, or by a parallel run): return that label.
                var labels = await ListLabelsAsync(service, ct);
                return GmailLabel.FindByName(labels, name)
                    ?? throw new InvalidOperationException("Gmail reported the label as existing but did not list it.", ex);
            }
        }, ct);
    }

    private static GmailLabel ToLabel(Label label) =>
        new(label.Id, label.Name ?? "", string.Equals(label.Type, "system", StringComparison.OrdinalIgnoreCase) ? GmailLabelType.System : GmailLabelType.User);

    public Task BatchModifyAsync(
        IReadOnlyList<string> ids, IReadOnlyList<string> addLabelIds, IReadOnlyList<string> removeLabelIds, CancellationToken ct)
    {
        GmailLimits.EnsureValidBatchModify(ids, addLabelIds, removeLabelIds);
        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunAsync(service => retry.ExecuteAsync(async token =>
        {
            await quota.AcquireAsync(GmailQuotaLimiter.BatchModifyUnits, token);
            var body = new BatchModifyMessagesRequest
            {
                Ids = [.. ids.Distinct(StringComparer.Ordinal)],
                AddLabelIds = addLabelIds.Count > 0 ? [.. addLabelIds] : null,
                RemoveLabelIds = removeLabelIds.Count > 0 ? [.. removeLabelIds] : null,
            };
            await service.Users.Messages.BatchModify(body, Me).ExecuteAsync(token);
            logger.LogInformation("Modified labels on {Count} Gmail messages", body.Ids.Count);
            return true;
        }, ct), ct);
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
