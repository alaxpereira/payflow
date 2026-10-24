namespace PayFlow.Ledger.Infrastructure.Outbox;

public sealed class OutboxMessage
{
    public const int MaxErrorLength = 2000;
    public const int MaxTraceParentLength = 55;

    public Guid Id { get; init; }
    public required string Type { get; init; }
    public required string Payload { get; init; }
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Contexto W3C do trace que gerou o evento. O relay publica bem depois de a requisição terminar, então é
    /// por aqui que o trace atravessa o outbox até o RabbitMQ e o processor.
    /// </summary>
    public string? TraceParent { get; init; }
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Quantas vezes a publicação falhou. Útil para alertar sobre mensagens presas.</summary>
    public int Attempts { get; set; }

    public string? Error { get; set; }

    public void MarkPublished(DateTimeOffset now)
    {
        ProcessedAt = now;
        Error = null;
    }

    public void MarkFailed(Exception exception)
    {
        Attempts++;
        Error = exception.Message.Length > MaxErrorLength ? exception.Message[..MaxErrorLength] : exception.Message;
    }
}
