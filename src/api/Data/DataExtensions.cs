using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GmailOrganiser.Data;

public static class DataExtensions
{
    public const string ConnectionStringName = "Default";

    /// <summary>
    /// Registers <see cref="AppDbContext"/> and the start-up migrator. Call before anything that needs a
    /// migrated database at start-up; hosted services start in registration order, before the web server.
    /// </summary>
    public static IServiceCollection AddAppDatabase(this IServiceCollection services)
    {
        services.AddOptions<DatabaseOptions>()
            .BindConfiguration(DatabaseOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The connection string is read when the context is built, so test hosts can override it.
        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"Connection string 'ConnectionStrings:{ConnectionStringName}' is not configured " +
                    $"(environment variable ConnectionStrings__{ConnectionStringName}).");
            }

            AppDbContext.Configure(options, connectionString);
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<DatabaseMigrator>();
        return services;
    }
}
