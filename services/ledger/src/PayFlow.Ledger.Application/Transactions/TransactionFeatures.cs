using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PayFlow.Ledger.Application.Abstractions;
using PayFlow.Ledger.Domain;
using PayFlow.Ledger.Domain.Common;

namespace PayFlow.Ledger.Application.Transactions;

public sealed record TransactionResponse(
    Guid Id,
    string Kind,
    Guid? SourceAccountId,
    Guid DestinationAccountId,
    long Amount,
    string Currency,
    DateTimeOffset CreatedAt)
{
    public static TransactionResponse From(LedgerTransaction transaction) =>
        new(transaction.Id,
            transaction.Kind.ToString(),
            transaction.SourceAccountId,
            transaction.DestinationAccountId,
            transaction.Amount,
            transaction.Currency,
            transaction.CreatedAt);
}

public sealed record DepositCommand(Guid AccountId, long Amount, string IdempotencyKey);

public sealed class DepositHandler(IdempotentTransactionRunner runner, TimeProvider clock)
{
    public Task<Result<IdempotentResult<TransactionResponse>>> HandleAsync(DepositCommand command, CancellationToken ct)
    {
        var hash = RequestHash.Of(nameof(DepositCommand), command.AccountId, command.Amount);

        return runner.RunAsync(
            command.IdempotencyKey,
            hash,
            [command.AccountId],
            locked => LedgerTransaction.Deposit(locked[0], command.Amount, command.IdempotencyKey, hash, clock.GetUtcNow()),
            ct);
    }
}

public sealed record TransferCommand(Guid SourceAccountId, Guid DestinationAccountId, long Amount, string IdempotencyKey);

public sealed class TransferHandler(IdempotentTransactionRunner runner, TimeProvider clock)
{
    public Task<Result<IdempotentResult<TransactionResponse>>> HandleAsync(TransferCommand command, CancellationToken ct)
    {
        var hash = RequestHash.Of(nameof(TransferCommand), command.SourceAccountId, command.DestinationAccountId, command.Amount);

        // Uma transferência para a mesma conta travaria uma só linha; deixa o domínio rejeitar com um erro claro.
        Guid[] accountIds = command.SourceAccountId == command.DestinationAccountId
            ? [command.SourceAccountId]
            : [command.SourceAccountId, command.DestinationAccountId];

        return runner.RunAsync(
            command.IdempotencyKey,
            hash,
            accountIds,
            locked =>
            {
                var source = locked.Single(a => a.Id == command.SourceAccountId);
                var destination = locked.Single(a => a.Id == command.DestinationAccountId);
                return LedgerTransaction.Transfer(source, destination, command.Amount, command.IdempotencyKey, hash, clock.GetUtcNow());
            },
            ct);
    }
}

public sealed class GetTransactionHandler(ILedgerTransactionRepository transactions)
{
    public async Task<Result<TransactionResponse>> HandleAsync(Guid transactionId, CancellationToken ct)
    {
        var transaction = await transactions.GetByIdAsync(transactionId, ct);
        return transaction is null
            ? LedgerErrors.TransactionNotFound(transactionId)
            : TransactionResponse.From(transaction);
    }
}

internal static class RequestHash
{
    public static string Of(params object[] parts)
    {
        var canonical = string.Join('|', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
