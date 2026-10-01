using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GmailOrganiser.Data;

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the app or needing a database.
/// Commands that connect (e.g. <c>database update</c>) take <c>--connection</c>.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(options, connectionString: null);
        return new AppDbContext(options.Options);
    }
}
