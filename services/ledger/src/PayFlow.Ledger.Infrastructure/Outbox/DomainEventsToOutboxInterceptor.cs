using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayFlow.Ledger.Domain.Common;

namespace PayFlow.Ledger.Infrastructure.Outbox;

/// <summary>
/// Converte eventos de domínio em linhas de outbox dentro do mesmo SaveChanges, para que os eventos sejam persistidos
/// de forma atômica com a mudança de estado que os gerou.
/// </summary>
internal sealed class DomainEventsToOutboxInterceptor : SaveChangesInterceptor
{
    // Enums como texto ("Transfer"), não como número: o payload é um contrato lido por outros serviços.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null)
            WriteOutboxMessages(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
            WriteOutboxMessages(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void WriteOutboxMessages(DbContext context)
    {
        var aggregates = context.ChangeTracker.Entries<AggregateRoot>()
            .Select(entry => entry.Entity)
            .Where(aggregate => aggregate.DomainEvents.Count > 0)
            .ToList();

        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                context.Add(new OutboxMessage
                {
                    Id = Guid.CreateVersion7(domainEvent.OccurredAt),
                    Type = domainEvent.GetType().Name,
                    Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonOptions),
                    OccurredAt = domainEvent.OccurredAt,
                });
            }

            aggregate.ClearDomainEvents();
        }
    }
}
