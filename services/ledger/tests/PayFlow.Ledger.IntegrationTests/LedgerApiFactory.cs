using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace PayFlow.Ledger.IntegrationTests;

/// <summary>Sobe a API real contra containers descartáveis de PostgreSQL e RabbitMQ.</summary>
public sealed class LedgerApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4-alpine").Build();

    public string RabbitMqUri => _rabbitMq.GetConnectionString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Ledger", _postgres.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("RabbitMq:Uri", RabbitMqUri);
        builder.UseSetting("Outbox:Relay:Enabled", "true");
        builder.UseSetting("Outbox:Relay:PollingInterval", "00:00:00.200");
    }

    public Task InitializeAsync() => Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbitMq.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class LedgerApiCollection : ICollectionFixture<LedgerApiFactory>
{
    public const string Name = "Ledger API";
}
