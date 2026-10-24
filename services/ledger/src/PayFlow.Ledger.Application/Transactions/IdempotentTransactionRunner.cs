using System.Diagnostics;
using PayFlow.Ledger.Application.Abstractions;
using PayFlow.Ledger.Application.Telemetry;
using PayFlow.Ledger.Domain;
using PayFlow.Ledger.Domain.Common;

namespace PayFlow.Ledger.Application.Transactions;

/// <summary>
/// Fluxo compartilhado por todo comando que altera saldo:
/// trava as contas, confere a chave de idempotência, executa a operação de domínio e salva, tudo numa só transação de banco.
/// </summary>
public sealed class IdempotentTransactionRunner(
    IAccountRepository accounts,
    ILedgerTransactionRepository transactions,
    IUnitOfWork unitOfWork,
    LedgerMetrics metrics)
{
    public async Task<Result<IdempotentResult<TransactionResponse>>> RunAsync(
        string idempotencyKey,
        string requestHash,
        IReadOnlyCollection<Guid> accountIds,
        Func<IReadOnlyList<Account>, Result<LedgerTransaction>> execute,
        CancellationToken ct)
    {
        var result = await ExecuteAsync(idempotencyKey, requestHash, accountIds, execute, ct);
        if (result.IsSuccess)
            Record(result.Value);
        return result;
    }

    // Registrado só depois do commit: uma transação revertida não pode aparecer nas métricas.
    private void Record(IdempotentResult<TransactionResponse> result)
    {
        var transaction = result.Value;
        Activity.Current?.SetTag("payflow.transaction.id", transaction.Id);
        Activity.Current?.SetTag("payflow.idempotent_replay", result.WasReplayed);

        if (result.WasReplayed)
            metrics.IdempotentReplay();
        else
            metrics.TransactionCompleted(transaction.Kind, transaction.Currency);
    }

    private async Task<Result<IdempotentResult<TransactionResponse>>> ExecuteAsync(
        string idempotencyKey,
        string requestHash,
        IReadOnlyCollection<Guid> accountIds,
        Func<IReadOnlyList<Account>, Result<LedgerTransaction>> execute,
        CancellationToken ct)
    {
        try
        {
            return await unitOfWork.ExecuteInTransactionAsync<Result<IdempotentResult<TransactionResponse>>>(async token =>
            {
                // Trava primeiro: uma retentativa da mesma requisição trava as mesmas contas, então espera a original
                // fazer commit e depois encontra a chave de idempotência logo abaixo.
                var locked = await accounts.GetForUpdateAsync(accountIds, token);

                var existing = await transactions.GetByIdempotencyKeyAsync(idempotencyKey, token);
                if (existing is not null)
                {
                    return existing.RequestHash == requestHash
                        ? IdempotentResult.Replayed(TransactionResponse.From(existing))
                        : LedgerErrors.IdempotencyKeyReused;
                }

                foreach (var id in accountIds)
                {
                    if (locked.All(a => a.Id != id))
                        return LedgerErrors.AccountNotFound(id);
                }

                var executed = execute(locked);
                if (executed.IsFailure)
                    return executed.Error!;

                transactions.Add(executed.Value);
                await unitOfWork.SaveChangesAsync(token);
                return IdempotentResult.Created(TransactionResponse.From(executed.Value));
            }, ct);
        }
        catch (UniqueConstraintViolationException)
        {
            // Mesma chave usada ao mesmo tempo em contas diferentes (por isso os locks de linha não as serializaram).
            return LedgerErrors.ConcurrentRequest;
        }
    }
}

public sealed record IdempotentResult<T>(T Value, bool WasReplayed);

public static class IdempotentResult
{
    public static Result<IdempotentResult<T>> Created<T>(T value) => new IdempotentResult<T>(value, WasReplayed: false);
    public static Result<IdempotentResult<T>> Replayed<T>(T value) => new IdempotentResult<T>(value, WasReplayed: true);
}
