using Microsoft.AspNetCore.Mvc;
using PayFlow.Ledger.Application.Transactions;

namespace PayFlow.Ledger.Api.Endpoints;

public sealed record TransferRequest(Guid SourceAccountId, Guid DestinationAccountId, long Amount);

internal static class TransactionEndpoints
{
    public static void MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/transfers", async (
            TransferRequest request,
            [FromHeader(Name = ResultExtensions.IdempotencyKeyHeader)] string? idempotencyKey,
            TransferHandler handler,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (ResultExtensions.ValidateIdempotencyKey(idempotencyKey) is { } invalid)
                return invalid;

            var command = new TransferCommand(request.SourceAccountId, request.DestinationAccountId, request.Amount, idempotencyKey!);
            return (await handler.HandleAsync(command, ct)).ToTransactionResult(http);
        }).WithTags("Transfers");

        app.MapGet("/transactions/{transactionId:guid}", async (Guid transactionId, GetTransactionHandler handler, CancellationToken ct) =>
            (await handler.HandleAsync(transactionId, ct)).ToOk())
            .WithTags("Transactions");
    }
}
