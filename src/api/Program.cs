using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Auth;
using GmailOrganiser.Health;
using GmailOrganiser.Llm;
using GmailOrganiser.Settings;

var builder = WebApplication.CreateBuilder(args);

builder.AddJsonConsoleLogging();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddApiSecurity(builder.Configuration);
builder.Services.AddAppDatabase();
builder.Services.AddHealthEndpoints();
builder.Services.AddSettings(builder.Configuration);
builder.Services.AddGmail();
builder.Services.AddLlm();

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
app.UseLlmNotConfiguredProblem();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthEndpoints();
app.MapSettingsEndpoints();
app.MapLlmEndpoints();
app.MapGmailAuthEndpoints();

// Hosted services start before the server listens: DatabaseMigrator applies the migrations first.
// A start-up failure is logged and propagates out of Run, so the process exits non-zero.
app.Run();
