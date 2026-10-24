using PayFlow.Ledger.Domain;

namespace PayFlow.Ledger.Application.Abstractions;

public interface IAccountRepository
{
    Task<Account?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Carrega as contas e trava as linhas até o fim da transação atual. Os locks são sempre obtidos na mesma ordem,
    /// então duas transações que mexem nas mesmas contas não entram em deadlock.
    /// </summary>
    Task<IReadOnlyList<Account>> GetForUpdateAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    void Add(Account account);
}

public interface ILedgerTransactionRepository
{
    Task<LedgerTransaction?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<LedgerTransaction?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct);
    void Add(LedgerTransaction transaction);
}

public interface IStatementReader
{
    Task<IReadOnlyList<StatementEntry>> GetEntriesAsync(Guid accountId, int page, int pageSize, CancellationToken ct);
}

public sealed record StatementEntry(Guid TransactionId, long Amount, long BalanceAfter, DateTimeOffset CreatedAt);

public interface IUnitOfWork
{
    /// <summary>Executa <paramref name="operation"/> dentro de uma transação de banco e faz commit quando ela retorna.</summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct);

    /// <exception cref="UniqueConstraintViolationException">Um índice único rejeitou a alteração.</exception>
    Task SaveChangesAsync(CancellationToken ct);
}

public sealed class UniqueConstraintViolationException(string? constraintName, Exception innerException)
    : Exception($"A constraint única '{constraintName}' foi violada.", innerException)
{
    public string? ConstraintName { get; } = constraintName;
}
