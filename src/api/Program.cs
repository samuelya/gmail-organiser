using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Health;
using GmailOrganiser.Settings;

var builder = WebApplication.CreateBuilder(args);

builder.AddJsonConsoleLogging();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddApiSecurity(builder.Configuration);
builder.Services.AddAppDatabase();
builder.Services.AddHealthEndpoints();
builder.Services.AddSettings(builder.Configuration);

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
app.MapSettingsEndpoints();

// Hosted services start before the server listens: DatabaseMigrator applies the migrations first.
// A start-up failure is logged and propagates out of Run, so the process exits non-zero.
app.Run();
