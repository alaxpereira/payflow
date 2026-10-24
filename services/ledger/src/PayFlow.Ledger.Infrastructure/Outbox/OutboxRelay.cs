using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayFlow.Ledger.Application.Telemetry;
using PayFlow.Ledger.Infrastructure.Messaging;
using PayFlow.Ledger.Infrastructure.Persistence;
using RabbitMQ.Client;

namespace PayFlow.Ledger.Infrastructure.Outbox;

/// <summary>
/// Publica as mensagens do outbox no RabbitMQ. Garantia at-least-once: se o processo cair entre a confirmação do
/// broker e o commit, a mensagem é publicada de novo, então os consumidores precisam ser idempotentes
/// (o MessageId é estável: é o Id da linha do outbox).
/// </summary>
internal sealed class OutboxRelay(
    IServiceScopeFactory scopeFactory,
    RabbitMqConnection rabbitMq,
    IOptions<OutboxRelayOptions> options,
    LedgerMetrics metrics,
    TimeProvider clock,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Relay do outbox desativado.");
            return;
        }

        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await PublishBatchAsync(settings.BatchSize, stoppingToken);
                consecutiveFailures = 0;

                // Lote cheio: provavelmente há mais mensagens esperando, então segue sem pausa.
                if (published < settings.BatchSize)
                    await Task.Delay(settings.PollingInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var delay = BackoffFor(consecutiveFailures, settings.MaxRetryDelay);
                logger.LogWarning(ex, "Falha ao publicar o outbox ({Failures} seguidas). Nova tentativa em {Delay}.",
                    consecutiveFailures, delay);

                try
                {
                    await Task.Delay(delay, clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<int> PublishBatchAsync(int batchSize, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // SKIP LOCKED permite várias instâncias da API rodando o relay sem pegar as mesmas mensagens.
        var batch = await db.OutboxMessages
            .FromSql($"SELECT * FROM outbox_messages WHERE processed_at IS NULL ORDER BY occurred_at LIMIT {batchSize} FOR UPDATE SKIP LOCKED")
            .ToListAsync(ct);

        if (batch.Count == 0)
            return 0;

        var channel = await rabbitMq.GetPublishChannelAsync(ct);
        var published = 0;
        try
        {
            foreach (var message in batch)
            {
                await PublishAsync(channel, message, ct);

                var now = clock.GetUtcNow();
                message.MarkPublished(now);
                metrics.OutboxPublished(message.Type, now - message.OccurredAt);
                published++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Grava o que já foi publicado e registra a falha; a mensagem que falhou volta no próximo lote.
            batch[published].MarkFailed(ex);
            metrics.OutboxPublishFailed();
            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            throw;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogDebug("{Count} mensagens do outbox publicadas.", published);
        return published;
    }

    private static async Task PublishAsync(IChannel channel, OutboxMessage message, CancellationToken ct)
    {
        var routingKey = EventTopology.RoutingKeyFor(message.Type);

        // O span de publicação é filho do span da requisição que gravou o evento, mesmo rodando segundos depois.
        ActivityContext.TryParse(message.TraceParent, traceState: null, out var parent);
        using var activity = LedgerTelemetry.ActivitySource.StartActivity(
            $"{EventTopology.Exchange} publish", ActivityKind.Producer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.operation.type", "send");
        activity?.SetTag("messaging.destination.name", EventTopology.Exchange);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", routingKey);
        activity?.SetTag("messaging.message.id", message.Id.ToString());

        var properties = new BasicProperties
        {
            MessageId = message.Id.ToString(),
            Type = message.Type,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>(),
        };

        // Sem listener de traces (ex.: telemetria desligada) não há span, mas o contexto original segue adiante
        // para o processor continuar o mesmo trace.
        var traceParent = activity?.Id ?? message.TraceParent;
        if (traceParent is not null)
            properties.Headers["traceparent"] = traceParent;

        try
        {
            await channel.BasicPublishAsync(
                EventTopology.Exchange,
                routingKey,
                mandatory: false,
                properties,
                Encoding.UTF8.GetBytes(message.Payload),
                ct);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>Backoff exponencial: 1s, 2s, 4s... limitado a <paramref name="max"/>.</summary>
    internal static TimeSpan BackoffFor(int consecutiveFailures, TimeSpan max)
    {
        var seconds = Math.Pow(2, Math.Min(consecutiveFailures - 1, 16));
        return TimeSpan.FromSeconds(Math.Min(seconds, max.TotalSeconds));
    }
}
