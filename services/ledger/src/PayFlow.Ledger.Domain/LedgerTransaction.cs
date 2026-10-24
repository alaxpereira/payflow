using PayFlow.Ledger.Domain.Common;

namespace PayFlow.Ledger.Domain;

public enum TransactionKind
{
    Deposit = 1,
    Transfer = 2,
}

/// <summary>
/// Uma movimentação de dinheiro concluída. É dona dos lançamentos que gerou; numa transferência, eles sempre somam zero.
/// </summary>
public sealed class LedgerTransaction : AggregateRoot
{
    public const int MaxIdempotencyKeyLength = 100;

    private readonly List<LedgerEntry> _entries = [];

    private LedgerTransaction() { }

    public Guid Id { get; private set; }
    public TransactionKind Kind { get; private set; }
    public Guid? SourceAccountId { get; private set; }
    public Guid DestinationAccountId { get; private set; }
    public long Amount { get; private set; }
    public string Currency { get; private set; } = null!;
    public string IdempotencyKey { get; private set; } = null!;

    /// <summary>Impressão digital da requisição original, usada para rejeitar uma chave reutilizada com outro payload.</summary>
    public string RequestHash { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyCollection<LedgerEntry> Entries => _entries;

    public static Result<LedgerTransaction> Deposit(
        Account destination, long amount, string idempotencyKey, string requestHash, DateTimeOffset now)
    {
        if (amount <= 0)
            return LedgerErrors.InvalidAmount;

        var transaction = Create(TransactionKind.Deposit, source: null, destination, amount, idempotencyKey, requestHash, now);

        destination.Credit(amount);
        transaction.AddEntry(destination, amount, now);
        transaction.RaiseCompleted();
        return transaction;
    }

    public static Result<LedgerTransaction> Transfer(
        Account source, Account destination, long amount, string idempotencyKey, string requestHash, DateTimeOffset now)
    {
        if (amount <= 0)
            return LedgerErrors.InvalidAmount;
        if (source.Id == destination.Id)
            return LedgerErrors.SameAccount;
        if (source.Currency != destination.Currency)
            return LedgerErrors.CurrencyMismatch;

        // O débito é o único passo que pode falhar, e quando falha não altera o saldo.
        var debit = source.Debit(amount);
        if (debit.IsFailure)
            return debit.Error!;

        destination.Credit(amount);

        var transaction = Create(TransactionKind.Transfer, source, destination, amount, idempotencyKey, requestHash, now);
        transaction.AddEntry(source, -amount, now);
        transaction.AddEntry(destination, amount, now);
        transaction.RaiseCompleted();
        return transaction;
    }

    private static LedgerTransaction Create(
        TransactionKind kind, Account? source, Account destination, long amount,
        string idempotencyKey, string requestHash, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            Kind = kind,
            SourceAccountId = source?.Id,
            DestinationAccountId = destination.Id,
            Amount = amount,
            Currency = destination.Currency,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            CreatedAt = now,
        };

    private void AddEntry(Account account, long signedAmount, DateTimeOffset now) =>
        _entries.Add(new LedgerEntry(Id, account.Id, signedAmount, account.Balance, now));

    private void RaiseCompleted() =>
        Raise(new TransactionCompleted(Id, Kind, SourceAccountId, DestinationAccountId, Amount, Currency, CreatedAt));
}
