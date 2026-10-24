using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PayFlow.Ledger.Application.Telemetry;

/// <summary>Nomes das fontes de traces e métricas do ledger, registrados no OpenTelemetry pela API.</summary>
public static class LedgerTelemetry
{
    public const string ActivitySourceName = "PayFlow.Ledger";
    public const string MeterName = "PayFlow.Ledger";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}

/// <summary>Métricas de negócio do ledger. Os nomes seguem o padrão do OpenTelemetry (pontos, unidade separada).</summary>
public sealed class LedgerMetrics
{
    private static readonly double[] DelayBuckets = [0.01, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60];

    private readonly Counter<long> _transactions;
    private readonly Counter<long> _idempotentReplays;
    private readonly Counter<long> _outboxPublished;
    private readonly Counter<long> _outboxPublishFailures;
    private readonly Histogram<double> _outboxPublishDelay;

    public LedgerMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(LedgerTelemetry.MeterName);

        _transactions = meter.CreateCounter<long>("payflow.ledger.transactions", "{transaction}",
            "Transações concluídas.");
        _idempotentReplays = meter.CreateCounter<long>("payflow.ledger.idempotent_replays", "{request}",
            "Requisições repetidas respondidas pela idempotência, sem mover dinheiro.");
        _outboxPublished = meter.CreateCounter<long>("payflow.outbox.published", "{message}",
            "Mensagens do outbox confirmadas pelo broker.");
        _outboxPublishFailures = meter.CreateCounter<long>("payflow.outbox.publish_failures", "{failure}",
            "Falhas ao publicar mensagens do outbox.");
        _outboxPublishDelay = meter.CreateHistogram("payflow.outbox.publish_delay", "s",
            "Tempo entre a gravação do evento no outbox e a confirmação do broker.",
            tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DelayBuckets });
    }

    public void TransactionCompleted(string kind, string currency) =>
        _transactions.Add(1, new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("currency", currency));

    public void IdempotentReplay() => _idempotentReplays.Add(1);

    public void OutboxPublished(string eventType, TimeSpan delay)
    {
        var tag = new KeyValuePair<string, object?>("event_type", eventType);
        _outboxPublished.Add(1, tag);
        _outboxPublishDelay.Record(Math.Max(delay.TotalSeconds, 0), tag);
    }

    public void OutboxPublishFailed() => _outboxPublishFailures.Add(1);
}
