namespace PayFlow.Ledger.Infrastructure.Outbox;

public sealed class OutboxMessage
{
    public const int MaxErrorLength = 2000;

    public Guid Id { get; init; }
    public required string Type { get; init; }
    public required string Payload { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
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
