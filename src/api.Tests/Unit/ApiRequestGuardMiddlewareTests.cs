using GmailOrganiser.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Unit;

public sealed class ApiRequestGuardMiddlewareTests
{
    private const string Allowed = "http://app.example.com";

    [Theory]
    [InlineData("GET", "/api/things")]
    [InlineData("HEAD", "/api/things")]
    [InlineData("OPTIONS", "/api/things")]
    [InlineData("POST", "/healthz")]
    [InlineData("POST", "/apiary")]
    public async Task Requests_outside_scope_pass_without_header(string method, string path)
    {
        var (context, called) = await Invoke(method, path, requestedWith: null, origin: "http://evil.example.com");

        called.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task State_changing_request_without_header_is_forbidden(string method)
    {
        var (context, called) = await Invoke(method, "/api/things", requestedWith: null, origin: null);

        called.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Blank_header_is_forbidden()
    {
        var (context, called) = await Invoke("POST", "/api/things", requestedWith: "  ", origin: null);

        called.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData("http://evil.example.com")]
    [InlineData("null")]
    [InlineData("https://app.example.com")]
    [InlineData("http://app.example.com:8080")]
    public async Task Foreign_origin_is_forbidden(string origin)
    {
        var (context, called) = await Invoke("POST", "/api/things", requestedWith: "XMLHttpRequest", origin: origin);

        called.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Allowed)]
    [InlineData("HTTP://APP.EXAMPLE.COM")]
    public async Task Header_with_allowed_or_missing_origin_passes(string? origin)
    {
        var (context, called) = await Invoke("POST", "/api/things", requestedWith: "XMLHttpRequest", origin: origin);

        called.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Rejected_request_accepting_only_text_plain_is_still_403()
    {
        var (context, called) = await Invoke("POST", "/api/things", requestedWith: null, origin: null, accept: "text/plain");

        called.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData("http://APP.example.com/")]
    [InlineData(" http://app.example.com:80 ")]
    public async Task Configured_origins_are_normalised_to_browser_form(string configured)
    {
        var (context, called) = await Invoke("POST", "/api/things", requestedWith: "XMLHttpRequest", origin: Allowed, allowed: configured);

        called.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("http://localhost:4200", "http://localhost:4200")]
    [InlineData("HTTPS://Example.COM:443/", "https://example.com")]
    [InlineData("http://example.com:8080", "http://example.com:8080")]
    public void TryNormaliseOrigin_accepts_origins(string value, string expected)
    {
        SecurityOptions.TryNormaliseOrigin(value, out var origin).ShouldBeTrue();
        origin.ShouldBe(expected);
    }

    [Theory]
    [InlineData("http://localhost:4200/app")]
    [InlineData("http://localhost:4200?x=1")]
    [InlineData("http://localhost:4200#x")]
    [InlineData("http://user:pw@localhost:4200")]
    [InlineData("ftp://example.com")]
    [InlineData("localhost:4200")]
    [InlineData("")]
    public void TryNormaliseOrigin_rejects_non_origins(string value) =>
        SecurityOptions.TryNormaliseOrigin(value, out _).ShouldBeFalse();

    private static async Task<(HttpContext Context, bool Called)> Invoke(
        string method, string path, string? requestedWith, string? origin, string? accept = null, string allowed = Allowed)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = path;
        if (requestedWith is not null)
        {
            context.Request.Headers[ApiRequestGuardMiddleware.RequestedWithHeader] = requestedWith;
        }

        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        if (accept is not null)
        {
            context.Request.Headers.Accept = accept;
        }

        var called = false;
        var middleware = new ApiRequestGuardMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            Options.Create(new SecurityOptions { AllowedOrigins = [allowed] }));

        await middleware.InvokeAsync(context, services.GetRequiredService<Microsoft.AspNetCore.Http.IProblemDetailsService>());
        return (context, called);
    }
}
