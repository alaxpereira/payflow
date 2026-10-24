using PayFlow.Ledger.Domain.Common;

namespace PayFlow.Ledger.Domain;

public sealed class Account : AggregateRoot
{
    public const int MaxOwnerNameLength = 200;

    private static readonly HashSet<string> SupportedCurrencies = ["BRL", "USD", "EUR"];

    private Account() { }

    public Guid Id { get; private set; }
    public string OwnerName { get; private set; } = null!;
    public string Currency { get; private set; } = null!;

    /// <summary>Saldo em centavos. Nunca é negativo.</summary>
    public long Balance { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Result<Account> Open(string? ownerName, string? currency, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(ownerName))
            return LedgerErrors.OwnerNameRequired;

        var trimmedName = ownerName.Trim();
        if (trimmedName.Length > MaxOwnerNameLength)
            return LedgerErrors.OwnerNameTooLong;

        var currencyCode = currency?.Trim().ToUpperInvariant();
        if (currencyCode is null || !SupportedCurrencies.Contains(currencyCode))
            return LedgerErrors.UnsupportedCurrency(currency);

        var account = new Account
        {
            Id = Guid.CreateVersion7(now),
            OwnerName = trimmedName,
            Currency = currencyCode,
            Balance = 0,
            CreatedAt = now,
        };
        account.Raise(new AccountOpened(account.Id, account.Currency, now));
        return account;
    }

    // O saldo só muda via LedgerTransaction, que registra os lançamentos correspondentes.
    internal void Credit(long amount) => Balance = checked(Balance + amount);

    internal Result Debit(long amount)
    {
        if (amount > Balance)
            return LedgerErrors.InsufficientFunds(Id);

        Balance -= amount;
        return Result.Success();
    }
}
