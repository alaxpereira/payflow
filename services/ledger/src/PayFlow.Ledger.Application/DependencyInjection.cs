using Microsoft.Extensions.DependencyInjection;
using PayFlow.Ledger.Application.Accounts;
using PayFlow.Ledger.Application.Transactions;

namespace PayFlow.Ledger.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<OpenAccountHandler>();
        services.AddScoped<GetAccountHandler>();
        services.AddScoped<GetStatementHandler>();
        services.AddScoped<IdempotentTransactionRunner>();
        services.AddScoped<DepositHandler>();
        services.AddScoped<TransferHandler>();
        services.AddScoped<GetTransactionHandler>();
        return services;
    }
}
