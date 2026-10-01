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

    private static async Task<(HttpContext Context, bool Called)> Invoke(string method, string path, string? requestedWith, string? origin)
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

        var called = false;
        var middleware = new ApiRequestGuardMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            Options.Create(new SecurityOptions { AllowedOrigins = [Allowed] }));

        await middleware.InvokeAsync(context, services.GetRequiredService<Microsoft.AspNetCore.Http.IProblemDetailsService>());
        return (context, called);
    }
}
