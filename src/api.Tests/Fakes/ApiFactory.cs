using GmailOrganiser.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Fakes;

/// <summary>
/// Test host with a synthetic allowed origin and a test-only endpoint at
/// <see cref="TestEndpointPath"/> that is appended after the app pipeline, backed by the
/// collection's <see cref="PostgresFixture"/> database.
/// </summary>
public sealed class ApiFactory(PostgresFixture postgres) : WebApplicationFactory<Program>
{
    public const string AllowedOrigin = "http://app.example.com";
    public const string TestEndpointPath = "/api/test-only/echo";
    public const string EnvOllamaBaseUrl = "http://ollama.example.com:11434";

    /// <summary>Throwaway Data Protection key ring for this host.</summary>
    public string KeysPath { get; } = Path.Combine(Path.GetTempPath(), "gmo-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Security:AllowedOrigins:0", AllowedOrigin);
        builder.UseSetting("ConnectionStrings:Default", postgres.ConnectionString);
        builder.UseSetting("DataProtection:KeysPath", KeysPath);
        // Pin the .env values so the developer's or runner's environment never leaks into tests.
        builder.UseSetting("OLLAMA_BASE_URL", EnvOllamaBaseUrl);
        builder.UseSetting("GOOGLE_CLIENT_ID", "");
        builder.UseSetting("GOOGLE_CLIENT_SECRET", "");
        builder.UseSetting("GMAIL_FAKE", "");
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, TestEndpointStartupFilter>());
    }

    private sealed class TestEndpointStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Map(TestEndpointPath, branch => branch.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            }));
        };
    }
}
