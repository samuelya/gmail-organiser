using System.Net;
using System.Text.Json;
using GmailOrganiser.Llm.ClaudeApi;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Tests.Unit;

/// <summary>How SDK errors, odd error bodies and unreadable answers map to key-free <see cref="HttpRequestException"/>s.</summary>
public sealed partial class ClaudeApiChatTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authentication_error")]
    [InlineData(HttpStatusCode.Forbidden, "permission_error")]
    public async Task A_rejected_key_is_not_retried_and_never_echoed(HttpStatusCode status, string type)
    {
        api.Error(status, type);
        using var chat = Create();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.StatusCode.ShouldBe(status);
        ex.Message.ShouldContain("rejected the API key");
        ex.ToString().ShouldNotContain(ApiKey);
        ex.ToString().ShouldNotContain("x-api-key", Case.Insensitive);
        api.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_bad_request_shows_anthropics_error_message_and_is_not_retried()
    {
        api.Error(HttpStatusCode.BadRequest, "invalid_request_error");
        using var chat = Create();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ex.Message.ShouldContain("HTTP 400");
        ex.Message.ShouldContain("synthetic error");
        ex.ToString().ShouldNotContain(ApiKey);
        api.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "not_found_error")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "request_too_large")]
    public async Task Any_other_client_error_shows_anthropics_error_message(HttpStatusCode status, string type)
    {
        api.Error(status, type);
        using var chat = Create();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.Message.ShouldStartWith($"The Claude API answered HTTP {(int)status}");
        ex.Message.ShouldEndWith(": synthetic error.");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"synthetic\"")]
    [InlineData("""{"error":"synthetic"}""")]
    [InlineData("""{"error":[]}""")]
    [InlineData("""{"error":{"type":"invalid_request_error"}}""")]
    [InlineData("""{"error":{"type":"invalid_request_error","message":" \r\n "}}""")]
    public async Task A_client_error_without_a_message_keeps_the_short_text(string body)
    {
        api.ErrorBody(HttpStatusCode.BadRequest, body);
        using var chat = Create();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.Message.ShouldStartWith("The Claude API answered HTTP 400");
        ex.Message.ShouldEndWith(".");
        ex.Message.ShouldNotContain(":");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authentication_error")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limit_error")]
    public async Task Key_and_rate_limit_errors_never_include_the_body(HttpStatusCode status, string type)
    {
        api.Error(status, type, ("retry-after", "0"));
        using var chat = Create(maxRetries: 0);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.Message.ShouldNotContain("synthetic error");
    }

    [Fact]
    public async Task An_error_message_is_kept_on_one_line()
    {
        api.ErrorBody(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { type = "error", error = new { type = "invalid_request_error", message = "line one\r\nINFO forged\tline\u0000 two." } }));
        using var chat = Create();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.Message.ShouldEndWith("): line one INFO forged line two.");
    }

    [Fact]
    public async Task A_long_error_message_is_truncated_without_splitting_a_surrogate_pair()
    {
        // 199 letters then an emoji: a cut at 200 chars would land between its two halves.
        api.ErrorBody(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { type = "error", error = new { type = "invalid_request_error", message = new string('x', 199) + "\U0001F600" + new string('y', 50) } }));
        using var chat = Create();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.Message.ShouldEndWith(new string('x', 199) + "….");
    }

    [Fact]
    public async Task An_unreadable_answer_surfaces_as_a_key_free_http_error()
    {
        api.Unreadable();
        using var chat = Create();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.Message.ShouldBe(ClaudeApiErrorClient.UnreadableAnswer);
        ex.ToString().ShouldNotContain(ApiKey);
    }

    [Theory]
    [InlineData("<html>gateway</html>")]
    [InlineData("")]
    [InlineData("{\"type\":\"message\"")]
    public async Task A_non_json_answer_surfaces_as_a_key_free_http_error(string body)
    {
        api.ErrorBody(HttpStatusCode.OK, body);
        using var chat = Create();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.Message.ShouldBe(ClaudeApiErrorClient.UnreadableAnswer);
        ex.ToString().ShouldNotContain(ApiKey);
    }

    [Fact]
    public void A_json_error_after_the_callers_cancel_is_not_mapped()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        ClaudeApiErrorClient.Map(new JsonException(), TimeSpan.FromSeconds(1), cts.Token).ShouldBeNull();
        ClaudeApiErrorClient.Map(new JsonException(), TimeSpan.FromSeconds(1), CancellationToken.None)!.Message.ShouldBe(ClaudeApiErrorClient.UnreadableAnswer);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "[]")]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":"synthetic"}""")]
    [InlineData(HttpStatusCode.BadGateway, """{"type":"error","error":[]}""")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "<html>gateway</html>")]
    public async Task A_server_error_with_an_odd_body_keeps_the_status(HttpStatusCode status, string body)
    {
        api.ErrorBody(status, body);
        using var chat = Create(maxRetries: 0);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.StatusCode.ShouldBe(status);
        ex.Message.ShouldBe($"The Claude API answered HTTP {(int)status}.");
    }
}
