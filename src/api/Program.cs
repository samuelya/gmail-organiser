using GmailOrganiser.Common;
using GmailOrganiser.Health;

var builder = WebApplication.CreateBuilder(args);

builder.AddJsonConsoleLogging();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddApiSecurity(builder.Configuration);
builder.Services.AddHealthEndpoints();

var app = builder.Build();

// Host filtering (AllowedHosts) is applied by the default WebApplication pipeline.
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseApiRequestGuard();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthEndpoints();

app.Run();

public partial class Program;
