using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace PayFlow.Ledger.Infrastructure.Messaging;

/// <summary>
/// Conexão e canal de publicação criados sob demanda: a API sobe mesmo com o broker fora do ar, e o relay
/// reconecta na próxima tentativa.
/// </summary>
internal sealed class RabbitMqConnection(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnection> logger)
    : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task<IChannel> GetPublishChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
            return _channel;

        await _lock.WaitAsync(ct);
        try
        {
            if (_channel is { IsOpen: true })
                return _channel;

            if (_connection is not { IsOpen: true })
            {
                if (_connection is not null)
                    await _connection.DisposeAsync();

                var uri = options.Value.Uri
                    ?? throw new InvalidOperationException($"'{RabbitMqOptions.SectionName}:Uri' não está configurado.");
                var factory = new ConnectionFactory { Uri = new Uri(uri), ClientProvidedName = "payflow-ledger" };
                _connection = await factory.CreateConnectionAsync(ct);
            }

            if (_channel is not null)
                await _channel.DisposeAsync();

            // Com o rastreamento de confirms ligado, BasicPublishAsync só retorna depois que o broker confirma
            // a mensagem, e lança exceção se ela for recusada.
            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                ct);
            await EventTopology.DeclareAsync(_channel, ct);

            logger.LogInformation("Canal de publicação no RabbitMQ aberto.");
            return _channel;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
            await _channel.DisposeAsync();
        if (_connection is not null)
            await _connection.DisposeAsync();
        _lock.Dispose();
    }
}
