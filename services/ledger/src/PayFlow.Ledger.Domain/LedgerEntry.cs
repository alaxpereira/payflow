namespace PayFlow.Ledger.Domain;

/// <summary>Uma linha do extrato de uma conta. Imutável depois de gravada.</summary>
public sealed class LedgerEntry
{
    private LedgerEntry() { }

    internal LedgerEntry(Guid transactionId, Guid accountId, long amount, long balanceAfter, DateTimeOffset createdAt)
    {
        Id = Guid.CreateVersion7(createdAt);
        TransactionId = transactionId;
        AccountId = accountId;
        Amount = amount;
        BalanceAfter = balanceAfter;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid TransactionId { get; private set; }
    public Guid AccountId { get; private set; }

    /// <summary>Valor com sinal, em centavos: positivo para créditos, negativo para débitos.</summary>
    public long Amount { get; private set; }

    public long BalanceAfter { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
