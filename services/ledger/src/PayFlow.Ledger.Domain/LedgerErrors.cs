using PayFlow.Ledger.Domain.Common;

namespace PayFlow.Ledger.Domain;

public static class LedgerErrors
{
    public static readonly Error OwnerNameRequired =
        Error.Validation("account.owner_name_required", "O nome do titular é obrigatório.");

    public static readonly Error OwnerNameTooLong =
        Error.Validation("account.owner_name_too_long", $"O nome do titular deve ter no máximo {Account.MaxOwnerNameLength} caracteres.");

    public static readonly Error InvalidAmount =
        Error.Validation("transaction.invalid_amount", "O valor deve ser um número positivo em centavos.");

    public static readonly Error SameAccount =
        Error.Validation("transfer.same_account", "As contas de origem e destino devem ser diferentes.");

    public static readonly Error CurrencyMismatch =
        Error.BusinessRule("transfer.currency_mismatch", "As contas de origem e destino devem usar a mesma moeda.");

    public static readonly Error IdempotencyKeyReused =
        Error.Conflict("idempotency.key_reused", "Esta Idempotency-Key já foi usada com uma requisição diferente.");

    public static readonly Error ConcurrentRequest =
        Error.Conflict("idempotency.concurrent_request", "Uma requisição com esta Idempotency-Key já está sendo processada. Tente novamente.");

    public static Error UnsupportedCurrency(string? currency) =>
        Error.Validation("account.unsupported_currency", $"A moeda '{currency}' não é suportada.");

    public static Error AccountNotFound(Guid accountId) =>
        Error.NotFound("account.not_found", $"A conta '{accountId}' não foi encontrada.");

    public static Error TransactionNotFound(Guid transactionId) =>
        Error.NotFound("transaction.not_found", $"A transação '{transactionId}' não foi encontrada.");

    public static Error InsufficientFunds(Guid accountId) =>
        Error.BusinessRule("account.insufficient_funds", $"A conta '{accountId}' não tem saldo suficiente.");
}
