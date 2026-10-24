using PayFlow.Ledger.Infrastructure.Messaging;
using PayFlow.Ledger.Infrastructure.Outbox;

namespace PayFlow.Ledger.UnitTests.Infrastructure;

public class OutboxRelayTests
{
    [Theory]
    [InlineData("TransactionCompleted", "ledger.transaction_completed")]
    [InlineData("AccountOpened", "ledger.account_opened")]
    public void Routing_key_e_derivada_do_tipo_do_evento(string eventType, string expected)
    {
        Assert.Equal(expected, EventTopology.RoutingKeyFor(eventType));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(1_000, 30)]
    public void Backoff_cresce_exponencialmente_ate_o_limite(int failures, int expectedSeconds)
    {
        var delay = OutboxRelay.BackoffFor(failures, TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void Falha_incrementa_tentativas_e_trunca_a_mensagem_de_erro()
    {
        var message = new OutboxMessage { Type = "TransactionCompleted", Payload = "{}" };

        message.MarkFailed(new InvalidOperationException(new string('x', OutboxMessage.MaxErrorLength + 50)));
        message.MarkFailed(new InvalidOperationException("broker fora do ar"));

        Assert.Equal(2, message.Attempts);
        Assert.Equal("broker fora do ar", message.Error);
        Assert.Null(message.ProcessedAt);
    }
}
