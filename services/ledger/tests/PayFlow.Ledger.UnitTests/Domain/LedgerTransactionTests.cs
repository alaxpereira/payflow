using PayFlow.Ledger.Domain;

namespace PayFlow.Ledger.UnitTests.Domain;

public class LedgerTransactionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Deposito_credita_a_conta_e_gera_um_lancamento()
    {
        var account = OpenAccount();

        var deposit = LedgerTransaction.Deposit(account, 10_000, "key-1", "hash", Now);

        Assert.True(deposit.IsSuccess);
        Assert.Equal(10_000, account.Balance);
        var entry = Assert.Single(deposit.Value.Entries);
        Assert.Equal(10_000, entry.Amount);
        Assert.Equal(10_000, entry.BalanceAfter);
        Assert.Null(deposit.Value.SourceAccountId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Deposito_rejeita_valor_nao_positivo(long amount)
    {
        var account = OpenAccount();

        var deposit = LedgerTransaction.Deposit(account, amount, "key-1", "hash", Now);

        Assert.Equal(LedgerErrors.InvalidAmount, deposit.Error);
        Assert.Equal(0, account.Balance);
    }

    [Fact]
    public void Transferencia_move_dinheiro_com_lancamentos_balanceados()
    {
        var source = OpenAccountWithBalance(10_000);
        var destination = OpenAccount();

        var transfer = LedgerTransaction.Transfer(source, destination, 2_500, "key-2", "hash", Now);

        Assert.True(transfer.IsSuccess);
        Assert.Equal(7_500, source.Balance);
        Assert.Equal(2_500, destination.Balance);
        Assert.Equal(2, transfer.Value.Entries.Count);
        Assert.Equal(0, transfer.Value.Entries.Sum(e => e.Amount));
        Assert.Contains(transfer.Value.Entries, e => e.AccountId == source.Id && e.BalanceAfter == 7_500);
        Assert.Contains(transfer.Value.Entries, e => e.AccountId == destination.Id && e.BalanceAfter == 2_500);

        var completed = Assert.IsType<TransactionCompleted>(Assert.Single(transfer.Value.DomainEvents));
        Assert.Equal(TransactionKind.Transfer, completed.Kind);
        Assert.Equal(2_500, completed.Amount);
    }

    [Fact]
    public void Transferencia_sem_saldo_nao_altera_nenhum_dos_saldos()
    {
        var source = OpenAccountWithBalance(1_000);
        var destination = OpenAccount();

        var transfer = LedgerTransaction.Transfer(source, destination, 1_001, "key-2", "hash", Now);

        Assert.Equal("account.insufficient_funds", transfer.Error?.Code);
        Assert.Equal(1_000, source.Balance);
        Assert.Equal(0, destination.Balance);
    }

    [Fact]
    public void Transferencia_para_a_mesma_conta_e_rejeitada()
    {
        var account = OpenAccountWithBalance(1_000);

        var transfer = LedgerTransaction.Transfer(account, account, 100, "key-2", "hash", Now);

        Assert.Equal(LedgerErrors.SameAccount, transfer.Error);
        Assert.Equal(1_000, account.Balance);
    }

    [Fact]
    public void Transferencia_entre_moedas_diferentes_e_rejeitada()
    {
        var source = OpenAccountWithBalance(1_000);
        var destination = Account.Open("John", "USD", Now).Value;

        var transfer = LedgerTransaction.Transfer(source, destination, 100, "key-2", "hash", Now);

        Assert.Equal(LedgerErrors.CurrencyMismatch, transfer.Error);
        Assert.Equal(1_000, source.Balance);
    }

    private static Account OpenAccount() => Account.Open("Maria", "BRL", Now).Value;

    private static Account OpenAccountWithBalance(long balance)
    {
        var account = OpenAccount();
        LedgerTransaction.Deposit(account, balance, $"seed-{Guid.NewGuid()}", "hash", Now);
        return account;
    }
}
