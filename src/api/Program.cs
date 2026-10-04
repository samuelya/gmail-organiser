using GmailOrganiser.Analysis;
using GmailOrganiser.Claude;
using GmailOrganiser.CleanUp;
using GmailOrganiser.CleanUp.Unsubscribe;
using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Auth;
using GmailOrganiser.Health;
using GmailOrganiser.Jobs;
using GmailOrganiser.Llm;
using GmailOrganiser.Mcp;
using GmailOrganiser.Memory;
using GmailOrganiser.Review;
using GmailOrganiser.Rules;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using GmailOrganiser.Setup;

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
builder.Services.AddSetup();
builder.Services.AddJobs();
builder.Services.AddFetch();
builder.Services.AddAnalysis();
builder.Services.AddMemory();
builder.Services.AddReview();
builder.Services.AddClaude();
builder.Services.AddCleanUp();
builder.Services.AddRules();
builder.Services.AddUnsubscribe();
builder.Services.AddMcpServer();

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
app.MapSetupEndpoints();
app.MapJobsEndpoints();
app.MapFetchEndpoints();
app.MapSendersEndpoints();
app.MapAnalysisEndpoints();
app.MapReviewEndpoints();
app.MapLabelsEndpoints();
app.MapHistoryEndpoints();
app.MapCleanUpEndpoints();
app.MapClaudeReviewEndpoints();
app.MapUnsubscribeEndpoints();
app.MapRulesEndpoints();
app.MapAppsScriptEndpoints();
app.MapMcpServer();
app.MapHub<JobsHub>(JobsHub.Path);

// Hosted services start before the server listens: DatabaseMigrator applies the migrations first.
// A start-up failure is logged and propagates out of Run, so the process exits non-zero.
app.Run();
