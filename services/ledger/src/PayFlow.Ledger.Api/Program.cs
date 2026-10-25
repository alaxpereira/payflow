using Microsoft.EntityFrameworkCore;
using PayFlow.Ledger.Api.Endpoints;
using PayFlow.Ledger.Api.Security;
using PayFlow.Ledger.Api.Telemetry;
using PayFlow.Ledger.Application;
using PayFlow.Ledger.Infrastructure;
using PayFlow.Ledger.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.AddTelemetry();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddHealthChecks().AddDbContextCheck<LedgerDbContext>();
builder.Services.AddPerIpRateLimiting();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync();
}

app.MapHealthChecks("/health");
app.MapAccountEndpoints();
app.MapTransactionEndpoints();

await app.RunAsync();

// Expõe o entry point para o WebApplicationFactory nos testes de integração.
public partial class Program;
