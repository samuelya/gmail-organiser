using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Data;
using GmailOrganiser.Health;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class DatabaseStartupTests(PostgresFixture postgres)
{
    // Nothing listens on port 1, so connecting fails fast with "connection refused".
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unreachable;Username=unreachable;Timeout=2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Healthz_returns_503_when_the_database_is_unreachable()
    {
        // Start without the migrator to simulate the database going away after start-up.
        await using var factory = WithDatabase(UnreachableDatabase).WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var migrator = services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(DatabaseMigrator));
            services.Remove(migrator);
        }));

        var response = await factory.CreateClient().GetAsync("/healthz", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadFromJsonAsync<HealthDto>(Ct)).ShouldNotBeNull().Status.ShouldBe("unhealthy");
    }

    [Fact]
    public void Start_up_fails_when_the_database_cannot_be_migrated()
    {
        using var factory = WithDatabase(UnreachableDatabase, retries: 1);

        Should.Throw<NpgsqlException>(() => factory.CreateClient());
    }

    [Fact]
    public void Start_up_fails_without_a_connection_string()
    {
        using var factory = WithDatabase(connectionString: "");

        Should.Throw<InvalidOperationException>(() => factory.CreateClient()).Message.ShouldContain("ConnectionStrings:Default");
    }

    private WebApplicationFactory<Program> WithDatabase(string connectionString, int retries = 0) =>
        new ApiFactory(postgres).WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:Default", connectionString)
            .UseSetting("Database:MigrationRetries", retries.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .UseSetting("Database:MigrationRetryDelay", "00:00:00.010"));
}
