using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Common;
using GmailOrganiser.Health;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;

namespace GmailOrganiser.Tests.Integration;

public sealed class SkeletonTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Healthz_returns_ok()
    {
        var response = await factory.CreateClient().GetAsync("/healthz", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<HealthDto>(Ct);
        body.ShouldNotBeNull().Status.ShouldBe("ok");
    }

    [Fact]
    public async Task Foreign_host_gets_400()
    {
        var client = factory.CreateClient(new() { BaseAddress = new Uri("http://other.example.com") });

        var response = await client.GetAsync("/healthz", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_without_requested_with_header_gets_403_problem_details()
    {
        var response = await factory.CreateClient().PostAsync(ApiFactory.TestEndpointPath, null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Post_without_header_accepting_only_text_plain_gets_403_not_500()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiFactory.TestEndpointPath);
        request.Headers.Accept.ParseAdd("text/plain");

        var response = await factory.CreateClient().SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Ipv6_loopback_host_is_allowed()
    {
        var client = factory.CreateClient(new() { BaseAddress = new Uri("http://[::1]") });

        var response = await client.GetAsync("/healthz", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("http://localhost:4200/app")]
    [InlineData("http://localhost:4200?x=1")]
    [InlineData("http://user@localhost:4200")]
    [InlineData("ftp://localhost")]
    public void Start_up_fails_for_an_origin_a_browser_can_never_send(string origin)
    {
        using var badFactory = new ApiFactory().WithWebHostBuilder(b => b.UseSetting("Security:AllowedOrigins:0", origin));

        Should.Throw<Microsoft.Extensions.Options.OptionsValidationException>(() => badFactory.CreateClient());
    }

    [Fact]
    public async Task Post_with_foreign_origin_gets_403()
    {
        using var request = Post(origin: "http://evil.example.com");

        var response = await factory.CreateClient().SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Post_with_allowed_origin_and_header_passes()
    {
        using var request = Post(origin: ApiFactory.AllowedOrigin);

        var response = await factory.CreateClient().SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Post_with_header_and_no_origin_passes()
    {
        using var request = Post(origin: null);

        var response = await factory.CreateClient().SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Unknown_route_returns_problem_details()
    {
        var response = await factory.CreateClient().GetAsync("/api/does-not-exist", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task OpenApi_document_is_not_served_outside_development()
    {
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static HttpRequestMessage Post(string? origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ApiFactory.TestEndpointPath);
        request.Headers.Add(ApiRequestGuardMiddleware.RequestedWithHeader, "XMLHttpRequest");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return request;
    }
}
