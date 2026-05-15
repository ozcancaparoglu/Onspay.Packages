using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Onspay.Cqrs.Messaging;
using RabbitMQ.Client;

namespace Onspay.Infrastructure.Messaging.RabbitMq;

public sealed class RabbitMqPublisher : IRabbitMqPublisher, IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqPublisher> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private IConnection? _connection;
    private IChannel? _channel;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RabbitMqPublisher(
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqPublisher> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task PublishAsync<T>(
        T message,
        string messageType,
        string? messageId = null,
        string? correlationId = null,
        string? routingKey = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);

        await EnsureInitializedAsync(cancellationToken);

        // MessageId is critical - consumer uses it as the inbox idempotency key.
        // If caller doesn't supply one, generate a stable GUID.
        messageId ??= Guid.NewGuid().ToString("N");

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);

        var props = new BasicProperties
        {
            MessageId = messageId,
            Type = messageType,
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            CorrelationId = correlationId,
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };

        await _channel!.BasicPublishAsync(
            exchange: _options.Exchange,
            routingKey: routingKey ?? _options.RoutingKey,
            mandatory: false,
            basicProperties: props,
            body: payload,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Published message. MessageId: {MessageId}, Type: {MessageType}, RoutingKey: {RoutingKey}",
            messageId, messageType, routingKey ?? _options.RoutingKey);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true }) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsOpen: true }) return;

            var factory = new ConnectionFactory
            {
                HostName = _options.Host,
                Port = _options.Port,
                UserName = _options.Username,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(10)
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

            _logger.LogInformation("Publisher channel opened for exchange {Exchange}", _options.Exchange);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.CloseAsync();
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }

        _initLock.Dispose();
    }
}
