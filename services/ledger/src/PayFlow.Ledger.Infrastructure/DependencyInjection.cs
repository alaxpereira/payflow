using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.Ledger.Application.Abstractions;
using PayFlow.Ledger.Infrastructure.Messaging;
using PayFlow.Ledger.Infrastructure.Outbox;
using PayFlow.Ledger.Infrastructure.Persistence;

namespace PayFlow.Ledger.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Ledger";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<DomainEventsToOutboxInterceptor>();

        // A connection string é resolvida sob demanda para os testes poderem sobrescrever a configuração.
        services.AddDbContext<LedgerDbContext>((sp, options) =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException($"A connection string '{ConnectionStringName}' não está configurada.");

            options
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(sp.GetRequiredService<DomainEventsToOutboxInterceptor>());
        });

        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<ILedgerTransactionRepository, LedgerTransactionRepository>();
        services.AddScoped<IStatementReader, StatementReader>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddOptions<RabbitMqOptions>().BindConfiguration(RabbitMqOptions.SectionName);
        services.AddOptions<OutboxRelayOptions>().BindConfiguration(OutboxRelayOptions.SectionName);
        services.AddSingleton<RabbitMqConnection>();
        services.AddHostedService<OutboxRelay>();
        return services;
    }
}
