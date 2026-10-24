using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayFlow.Ledger.Application.Abstractions;
using PayFlow.Ledger.Domain;

namespace PayFlow.Ledger.Infrastructure.Persistence;

internal sealed class AccountRepository(LedgerDbContext db) : IAccountRepository
{
    public Task<Account?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<IReadOnlyList<Account>> GetForUpdateAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var idArray = ids.Distinct().ToArray();

        // ORDER BY id faz toda transação obter os locks de linha na mesma ordem, o que evita deadlock entre
        // transferências opostas (A -> B e B -> A).
        return await db.Accounts
            .FromSql($"SELECT * FROM accounts WHERE id = ANY({idArray}) ORDER BY id FOR UPDATE")
            .ToListAsync(ct);
    }

    public void Add(Account account) => db.Accounts.Add(account);
}

internal sealed class LedgerTransactionRepository(LedgerDbContext db) : ILedgerTransactionRepository
{
    public Task<LedgerTransaction?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Transactions.Include(t => t.Entries).FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<LedgerTransaction?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct) =>
        db.Transactions.FirstOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey, ct);

    public void Add(LedgerTransaction transaction) => db.Transactions.Add(transaction);
}

internal sealed class StatementReader(LedgerDbContext db) : IStatementReader
{
    public async Task<IReadOnlyList<StatementEntry>> GetEntriesAsync(Guid accountId, int page, int pageSize, CancellationToken ct) =>
        await db.Entries
            .AsNoTracking()
            .Where(e => e.AccountId == accountId)
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new StatementEntry(e.TransactionId, e.Amount, e.BalanceAfter, e.CreatedAt))
            .ToListAsync(ct);
}

internal sealed class UnitOfWork(LedgerDbContext db) : IUnitOfWork
{
    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await operation(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw new UniqueConstraintViolationException(pg.ConstraintName, ex);
        }
    }
}
