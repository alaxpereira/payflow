using PayFlow.Ledger.Application.Abstractions;
using PayFlow.Ledger.Domain;
using PayFlow.Ledger.Domain.Common;

namespace PayFlow.Ledger.Application.Accounts;

public sealed record AccountResponse(Guid Id, string OwnerName, string Currency, long Balance, DateTimeOffset CreatedAt)
{
    public static AccountResponse From(Account account) =>
        new(account.Id, account.OwnerName, account.Currency, account.Balance, account.CreatedAt);
}

public sealed record OpenAccountCommand(string? OwnerName, string? Currency);

public sealed class OpenAccountHandler(IAccountRepository accounts, IUnitOfWork unitOfWork, TimeProvider clock)
{
    public async Task<Result<AccountResponse>> HandleAsync(OpenAccountCommand command, CancellationToken ct)
    {
        var opened = Account.Open(command.OwnerName, command.Currency, clock.GetUtcNow());
        if (opened.IsFailure)
            return opened.Error!;

        accounts.Add(opened.Value);
        await unitOfWork.SaveChangesAsync(ct);
        return AccountResponse.From(opened.Value);
    }
}

public sealed class GetAccountHandler(IAccountRepository accounts)
{
    public async Task<Result<AccountResponse>> HandleAsync(Guid accountId, CancellationToken ct)
    {
        var account = await accounts.GetByIdAsync(accountId, ct);
        return account is null ? LedgerErrors.AccountNotFound(accountId) : AccountResponse.From(account);
    }
}

public sealed record StatementResponse(Guid AccountId, int Page, int PageSize, IReadOnlyList<StatementEntry> Entries);

public sealed class GetStatementHandler(IAccountRepository accounts, IStatementReader statements)
{
    public const int MaxPageSize = 100;

    public async Task<Result<StatementResponse>> HandleAsync(Guid accountId, int page, int pageSize, CancellationToken ct)
    {
        if (await accounts.GetByIdAsync(accountId, ct) is null)
            return LedgerErrors.AccountNotFound(accountId);

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var entries = await statements.GetEntriesAsync(accountId, page, pageSize, ct);
        return new StatementResponse(accountId, page, pageSize, entries);
    }
}
