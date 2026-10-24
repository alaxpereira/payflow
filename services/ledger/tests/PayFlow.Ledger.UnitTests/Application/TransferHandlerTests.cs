using PayFlow.Ledger.Application.Abstractions;
using PayFlow.Ledger.Application.Transactions;
using PayFlow.Ledger.Domain;

namespace PayFlow.Ledger.UnitTests.Application;

public class TransferHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryLedger _ledger = new();
    private readonly TransferHandler _handler;

    public TransferHandlerTests()
    {
        var runner = new IdempotentTransactionRunner(_ledger, _ledger, _ledger);
        _handler = new TransferHandler(runner, new FixedTimeProvider(Now));
    }

    [Fact]
    public async Task Executa_a_transferencia_e_salva()
    {
        var (source, destination) = SeedAccounts(sourceBalance: 5_000);

        var result = await _handler.HandleAsync(new TransferCommand(source.Id, destination.Id, 1_000, "key-1"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.WasReplayed);
        Assert.Equal(4_000, source.Balance);
        Assert.Single(_ledger.Transactions);
    }

    [Fact]
    public async Task Retentativa_com_mesma_chave_e_payload_repete_resposta_sem_mover_dinheiro_de_novo()
    {
        var (source, destination) = SeedAccounts(sourceBalance: 5_000);
        var command = new TransferCommand(source.Id, destination.Id, 1_000, "key-1");

        var first = await _handler.HandleAsync(command, CancellationToken.None);
        var retry = await _handler.HandleAsync(command, CancellationToken.None);

        Assert.True(retry.IsSuccess);
        Assert.True(retry.Value.WasReplayed);
        Assert.Equal(first.Value.Value.Id, retry.Value.Value.Id);
        Assert.Equal(4_000, source.Balance);
        Assert.Single(_ledger.Transactions);
    }

    [Fact]
    public async Task Mesma_chave_com_payload_diferente_e_rejeitada()
    {
        var (source, destination) = SeedAccounts(sourceBalance: 5_000);

        await _handler.HandleAsync(new TransferCommand(source.Id, destination.Id, 1_000, "key-1"), CancellationToken.None);
        var reused = await _handler.HandleAsync(new TransferCommand(source.Id, destination.Id, 2_000, "key-1"), CancellationToken.None);

        Assert.Equal(LedgerErrors.IdempotencyKeyReused, reused.Error);
        Assert.Equal(4_000, source.Balance);
    }

    [Fact]
    public async Task Conta_inexistente_retorna_nao_encontrada()
    {
        var (source, _) = SeedAccounts(sourceBalance: 5_000);
        var unknownId = Guid.NewGuid();

        var result = await _handler.HandleAsync(new TransferCommand(source.Id, unknownId, 1_000, "key-1"), CancellationToken.None);

        Assert.Equal(LedgerErrors.AccountNotFound(unknownId), result.Error);
        Assert.Empty(_ledger.Transactions);
    }

    [Fact]
    public async Task Chave_reutilizada_em_paralelo_detectada_pelo_banco_retorna_conflito()
    {
        var (source, destination) = SeedAccounts(sourceBalance: 5_000);
        _ledger.FailNextSaveWithUniqueViolation = true;

        var result = await _handler.HandleAsync(new TransferCommand(source.Id, destination.Id, 1_000, "key-1"), CancellationToken.None);

        Assert.Equal(LedgerErrors.ConcurrentRequest, result.Error);
    }

    private (Account Source, Account Destination) SeedAccounts(long sourceBalance)
    {
        var source = Account.Open("Maria", "BRL", Now).Value;
        var destination = Account.Open("João", "BRL", Now).Value;
        LedgerTransaction.Deposit(source, sourceBalance, "seed", "hash", Now);
        _ledger.Add(source);
        _ledger.Add(destination);
        return (source, destination);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Persistência falsa que guarda tudo em listas. Locks e transações não fazem nada.</summary>
    private sealed class InMemoryLedger : IAccountRepository, ILedgerTransactionRepository, IUnitOfWork
    {
        private readonly List<Account> _accounts = [];
        private readonly List<LedgerTransaction> _pending = [];

        public List<LedgerTransaction> Transactions { get; } = [];
        public bool FailNextSaveWithUniqueViolation { get; set; }

        public Task<Account?> GetByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(_accounts.SingleOrDefault(a => a.Id == id));

        public Task<IReadOnlyList<Account>> GetForUpdateAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Account>>(_accounts.Where(a => ids.Contains(a.Id)).ToList());

        public void Add(Account account) => _accounts.Add(account);

        Task<LedgerTransaction?> ILedgerTransactionRepository.GetByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Transactions.SingleOrDefault(t => t.Id == id));

        public Task<LedgerTransaction?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct) =>
            Task.FromResult(Transactions.SingleOrDefault(t => t.IdempotencyKey == idempotencyKey));

        public void Add(LedgerTransaction transaction) => _pending.Add(transaction);

        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
            operation(ct);

        public Task SaveChangesAsync(CancellationToken ct)
        {
            if (FailNextSaveWithUniqueViolation)
            {
                FailNextSaveWithUniqueViolation = false;
                _pending.Clear();
                throw new UniqueConstraintViolationException("ix_ledger_transactions_idempotency_key", new InvalidOperationException());
            }

            Transactions.AddRange(_pending);
            _pending.Clear();
            return Task.CompletedTask;
        }
    }
}
