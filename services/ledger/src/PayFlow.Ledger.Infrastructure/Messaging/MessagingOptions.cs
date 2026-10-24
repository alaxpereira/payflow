namespace PayFlow.Ledger.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>Ex.: amqp://usuario:senha@localhost:5672</summary>
    public string? Uri { get; set; }
}

public sealed class OutboxRelayOptions
{
    public const string SectionName = "Outbox:Relay";

    public bool Enabled { get; set; }
    public int BatchSize { get; set; } = 100;
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
}
