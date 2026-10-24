using PayFlow.Ledger.Application.Transactions;
using PayFlow.Ledger.Domain;
using PayFlow.Ledger.Domain.Common;

namespace PayFlow.Ledger.Api.Endpoints;

internal static class ResultExtensions
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string IdempotentReplayedHeader = "Idempotent-Replayed";

    public static IResult ToProblem(this Error error) =>
        Results.Problem(
            statusCode: error.Type switch
            {
                ErrorType.Validation => StatusCodes.Status400BadRequest,
                ErrorType.NotFound => StatusCodes.Status404NotFound,
                ErrorType.Conflict => StatusCodes.Status409Conflict,
                ErrorType.BusinessRule => StatusCodes.Status422UnprocessableEntity,
                _ => StatusCodes.Status500InternalServerError,
            },
            title: error.Code,
            detail: error.Message);

    public static IResult ToOk<T>(this Result<T> result) =>
        result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem();

    /// <summary>201 para uma transação nova; 200 com <c>Idempotent-Replayed: true</c> para uma repetida.</summary>
    public static IResult ToTransactionResult(this Result<IdempotentResult<TransactionResponse>> result, HttpContext http)
    {
        if (result.IsFailure)
            return result.Error!.ToProblem();

        var (transaction, wasReplayed) = result.Value;
        if (!wasReplayed)
            return Results.Created($"/transactions/{transaction.Id}", transaction);

        http.Response.Headers[IdempotentReplayedHeader] = "true";
        return Results.Ok(transaction);
    }

    public static IResult? ValidateIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > LedgerTransaction.MaxIdempotencyKeyLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [IdempotencyKeyHeader] =
                    [$"O header {IdempotencyKeyHeader} é obrigatório e deve ter no máximo {LedgerTransaction.MaxIdempotencyKeyLength} caracteres."],
            });
        }

        return null;
    }
}
