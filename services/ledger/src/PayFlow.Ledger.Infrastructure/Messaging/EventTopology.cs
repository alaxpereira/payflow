using System.Text;
using RabbitMQ.Client;

namespace PayFlow.Ledger.Infrastructure.Messaging;

/// <summary>
/// Exchanges compartilhadas entre os serviços. O processor em Go declara as mesmas exchanges com os mesmos
/// argumentos (ver docs/mensageria.md); se divergirem, o RabbitMQ recusa a declaração.
/// </summary>
public static class EventTopology
{
    public const string Exchange = "payflow.events";
    public const string UnroutedExchange = "payflow.events.unrouted";
    public const string UnroutedQueue = "payflow.events.unrouted";
    public const int UnroutedQueueMaxLength = 100_000;

    public static async Task DeclareAsync(IChannel channel, CancellationToken ct)
    {
        // Eventos sem nenhuma fila interessada vão para a alternate exchange em vez de serem descartados.
        await channel.ExchangeDeclareAsync(UnroutedExchange, ExchangeType.Fanout, durable: true, autoDelete: false,
            cancellationToken: ct);
        await channel.QueueDeclareAsync(UnroutedQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-max-length"] = UnroutedQueueMaxLength,
            },
            cancellationToken: ct);
        await channel.QueueBindAsync(UnroutedQueue, UnroutedExchange, routingKey: "", cancellationToken: ct);

        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["alternate-exchange"] = UnroutedExchange },
            cancellationToken: ct);
    }

    /// <summary>"TransactionCompleted" → "ledger.transaction_completed".</summary>
    public static string RoutingKeyFor(string eventType)
    {
        var key = new StringBuilder("ledger.", eventType.Length + 12);
        for (var i = 0; i < eventType.Length; i++)
        {
            var c = eventType[i];
            if (char.IsUpper(c) && i > 0)
                key.Append('_');
            key.Append(char.ToLowerInvariant(c));
        }

        return key.ToString();
    }
}
