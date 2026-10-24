using Microsoft.AspNetCore.Mvc;
using PayFlow.Ledger.Application.Accounts;
using PayFlow.Ledger.Application.Transactions;

namespace PayFlow.Ledger.Api.Endpoints;

public sealed record OpenAccountRequest(string? OwnerName, string? Currency);

public sealed record DepositRequest(long Amount);

internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var accounts = app.MapGroup("/accounts").WithTags("Accounts");

        accounts.MapPost("/", async (OpenAccountRequest request, OpenAccountHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(new OpenAccountCommand(request.OwnerName, request.Currency), ct);
            return result.IsSuccess
                ? Results.Created($"/accounts/{result.Value.Id}", result.Value)
                : result.Error!.ToProblem();
        });

        accounts.MapGet("/{accountId:guid}", async (Guid accountId, GetAccountHandler handler, CancellationToken ct) =>
            (await handler.HandleAsync(accountId, ct)).ToOk());

        accounts.MapGet("/{accountId:guid}/statement", async (
            Guid accountId, GetStatementHandler handler, CancellationToken ct, int page = 1, int pageSize = 20) =>
            (await handler.HandleAsync(accountId, page, pageSize, ct)).ToOk());

        accounts.MapPost("/{accountId:guid}/deposits", async (
            Guid accountId,
            DepositRequest request,
            [FromHeader(Name = ResultExtensions.IdempotencyKeyHeader)] string? idempotencyKey,
            DepositHandler handler,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (ResultExtensions.ValidateIdempotencyKey(idempotencyKey) is { } invalid)
                return invalid;

            var result = await handler.HandleAsync(new DepositCommand(accountId, request.Amount, idempotencyKey!), ct);
            return result.ToTransactionResult(http);
        });
    }
}
