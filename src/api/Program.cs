using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Health;

var builder = WebApplication.CreateBuilder(args);

builder.AddJsonConsoleLogging();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddApiSecurity(builder.Configuration);
builder.Services.AddAppDatabase();
builder.Services.AddHealthEndpoints();

var app = builder.Build();

// Host filtering (AllowedHosts) is applied by the default WebApplication pipeline.
// Development keeps the developer exception page (added by WebApplication); everywhere else
// unhandled exceptions become a ProblemDetails 500 without a stack trace.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
}

app.UseStatusCodePages();
app.UseApiRequestGuard();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthEndpoints();

try
{
    // Start-up runs the database migrations (DatabaseMigrator) before the server listens.
    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    // No half-started API: log and exit non-zero so compose / the IDE show the failure.
    app.Logger.LogCritical(ex, "API start-up or run failed; exiting");
    return 1;
}

public partial class Program;
