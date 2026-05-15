using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Onspay.Infrastructure.Inbox;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Onspay.Infrastructure.Messaging.RabbitMq;

public sealed class RabbitMqConsumer(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqConsumer> logger)
    : BackgroundService
{
    private const string DeathHeader = "x-death";
    private const string LastErrorHeader = "x-last-error";
    private const string FirstFailedAtHeader = "x-first-failed-at";

    private readonly RabbitMqOptions _options = options.Value;
    private IConnection? _connection;
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("RabbitMQ consumer service starting...");

        try
        {
            await InitializeRabbitMqAsync(stoppingToken);
            await StartConsumingAsync(stoppingToken);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("RabbitMQ consumer service is stopping...");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RabbitMQ consumer service encountered an error");
            throw;
        }
    }

    private async Task InitializeRabbitMqAsync(CancellationToken cancellationToken)
    {
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

        if (_options.AutoCreateTopology)
            await DeclareTopologyAsync(cancellationToken);

        await _channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: 1,
            global: false,
            cancellationToken: cancellationToken);

        logger.LogInformation(
            "RabbitMQ connection established. Exchange: {Exchange}, Queue: {Queue}, " +
            "Retry: {RetryQueue} (TTL {Ttl}ms), DLQ: {Dlq}, MaxRetries: {MaxRetries}",
            _options.Exchange, _options.Queue, _options.RetryQueue,
            _options.RetryDelayMs, _options.DeadLetterQueue, _options.MaxRetryCount);
    }

    private async Task DeclareTopologyAsync(CancellationToken cancellationToken)
    {
        if (_channel is null)
            throw new InvalidOperationException("Channel is not initialized");

        // Terminal DLQ (parking lot)
        await _channel.ExchangeDeclareAsync(
            exchange: _options.DeadLetterExchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

        await _channel.QueueDeclareAsync(
            queue: _options.DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken);

        await _channel.QueueBindAsync(
            queue: _options.DeadLetterQueue,
            exchange: _options.DeadLetterExchange,
            routingKey: _options.DeadLetterRoutingKey,
            cancellationToken: cancellationToken);

        // Retry exchange + passive delay queue (no consumer)
        await _channel.ExchangeDeclareAsync(
            exchange: _options.RetryExchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

        var retryQueueArgs = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = _options.Exchange,
            ["x-dead-letter-routing-key"] = _options.RoutingKey,
            ["x-message-ttl"] = _options.RetryDelayMs
        };

        await _channel.QueueDeclareAsync(
            queue: _options.RetryQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: retryQueueArgs,
            cancellationToken: cancellationToken);

        await _channel.QueueBindAsync(
            queue: _options.RetryQueue,
            exchange: _options.RetryExchange,
            routingKey: _options.RoutingKey,
            cancellationToken: cancellationToken);

        // Main exchange + main queue (DLX -> retry exchange for the delay loop)
        await _channel.ExchangeDeclareAsync(
            exchange: _options.Exchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

        var mainQueueArgs = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = _options.RetryExchange,
            ["x-dead-letter-routing-key"] = _options.RoutingKey
        };

        await _channel.QueueDeclareAsync(
            queue: _options.Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: mainQueueArgs,
            cancellationToken: cancellationToken);

        await _channel.QueueBindAsync(
            queue: _options.Queue,
            exchange: _options.Exchange,
            routingKey: _options.RoutingKey,
            cancellationToken: cancellationToken);
    }

    private async Task StartConsumingAsync(CancellationToken cancellationToken)
    {
        if (_channel is null)
            throw new InvalidOperationException("Channel is not initialized");

        var consumer = new AsyncEventingBasicConsumer(_channel);

        consumer.ReceivedAsync += async (_, ea) =>
        {
            var messageId = ea.BasicProperties.MessageId ?? ea.DeliveryTag.ToString();
            var messageType = ea.BasicProperties.Type ?? ea.RoutingKey;
            var deathCount = GetDeathCount(ea.BasicProperties);

            logger.LogDebug(
                "Received message. MessageId: {MessageId}, Type: {MessageType}, " +
                "RoutingKey: {RoutingKey}, DeathCount: {DeathCount}",
                messageId, messageType, ea.RoutingKey, deathCount);

            try
            {
                var payload = Encoding.UTF8.GetString(ea.Body.Span);
                ValidateEnvelope(messageId, messageType, payload);
                await WriteToInboxAsync(messageId, messageType, payload, cancellationToken);

                await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken);
                logger.LogDebug("Message {MessageId} written to inbox and acknowledged", messageId);
            }
            catch (Exception ex)
            {
                await HandleFailureAsync(ea, messageId, deathCount, ex, cancellationToken);
            }
        };

        await _channel.BasicConsumeAsync(
            queue: _options.Queue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken);

        logger.LogInformation("Started consuming from queue: {Queue}", _options.Queue);
    }

    private async Task HandleFailureAsync(
        BasicDeliverEventArgs ea,
        string messageId,
        int deathCount,
        Exception ex,
        CancellationToken cancellationToken)
    {
        if (_channel is null) return;

        var permanent = IsPermanentFailure(ex);
        var exhausted = deathCount >= _options.MaxRetryCount;

        if (permanent || exhausted)
        {
            logger.LogError(ex,
                "Message {MessageId} -> terminal DLQ. DeathCount: {Count}/{Max}, Permanent: {Permanent}",
                messageId, deathCount, _options.MaxRetryCount, permanent);

            await PublishToDeadLetterAsync(ea, deathCount, ex, cancellationToken);
            await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken);
            return;
        }

        logger.LogWarning(ex,
            "Message {MessageId} failed transiently. Cycling through retry queue. Attempt {Next}/{Max}",
            messageId, deathCount + 1, _options.MaxRetryCount);

        await _channel.BasicNackAsync(
            ea.DeliveryTag,
            multiple: false,
            requeue: false,
            cancellationToken);
    }

    private static void ValidateEnvelope(string messageId, string messageType, string payload)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw new ArgumentException("MessageId is required");

        if (string.IsNullOrWhiteSpace(messageType))
            throw new ArgumentException("Message Type header is required");

        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("Payload is empty");

        // Payload must be valid JSON.
        using var _ = JsonDocument.Parse(payload);
    }

    private async Task WriteToInboxAsync(
        string messageId,
        string messageType,
        string payload,
        CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IInboxDbContext>();

        var inboxMessage = InboxMessage.Create(messageId, messageType, payload);
        dbContext.InboxMessages.Add(inboxMessage);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Message {MessageId} of type {MessageType} written to inbox",
                messageId, messageType);
        }
        catch (DbUpdateException ex)
        {
            // Idempotency conflict or other DB issue. Log and skip to avoid blocking the queue.
            logger.LogDebug(
                "Message: {MessageId}, skipping, error: {Error}",
                messageId, ex.Message);
        }
    }

    private async Task PublishToDeadLetterAsync(
        BasicDeliverEventArgs ea,
        int deathCount,
        Exception ex,
        CancellationToken cancellationToken)
    {
        var headers = CopyHeaders(ea.BasicProperties.Headers);
        headers[LastErrorHeader] = Truncate($"{ex.GetType().Name}: {ex.Message}", 1000);
        headers["x-original-exchange"] = ea.Exchange;
        headers["x-original-routing-key"] = ea.RoutingKey;
        headers["x-dead-lettered-at"] = DateTimeOffset.UtcNow.ToString("O");
        headers["x-final-death-count"] = deathCount;

        if (!headers.ContainsKey(FirstFailedAtHeader))
            headers[FirstFailedAtHeader] = DateTimeOffset.UtcNow.ToString("O");

        var props = BuildProperties(ea.BasicProperties, headers);

        await _channel!.BasicPublishAsync(
            exchange: _options.DeadLetterExchange,
            routingKey: _options.DeadLetterRoutingKey,
            mandatory: false,
            basicProperties: props,
            body: ea.Body,
            cancellationToken: cancellationToken);
    }

    private static BasicProperties BuildProperties(
        IReadOnlyBasicProperties source,
        IDictionary<string, object?> headers) =>
        new()
        {
            MessageId = source.MessageId,
            Type = source.Type,
            ContentType = source.ContentType,
            ContentEncoding = source.ContentEncoding,
            CorrelationId = source.CorrelationId,
            DeliveryMode = DeliveryModes.Persistent,
            Headers = headers
        };

    private static Dictionary<string, object?> CopyHeaders(IDictionary<string, object?>? source) =>
        source is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(source);

    private int GetDeathCount(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is null) return 0;
        if (!properties.Headers.TryGetValue(DeathHeader, out var raw)) return 0;
        if (raw is not List<object> entries) return 0;

        foreach (var entry in entries)
        {
            if (entry is not IDictionary<string, object?> dict) continue;

            var queue = dict.TryGetValue("queue", out var q) ? DecodeString(q) : null;
            if (queue != _options.Queue) continue;

            if (dict.TryGetValue("count", out var countObj))
            {
                return countObj switch
                {
                    int i => i,
                    long l => (int)l,
                    _ => 0
                };
            }
        }

        return 0;
    }

    private static string? DecodeString(object? value) => value switch
    {
        string s => s,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        _ => value?.ToString()
    };

    private static bool IsPermanentFailure(Exception ex) => ex switch
    {
        JsonException => true,
        FormatException => true,
        ArgumentNullException => true,
        ArgumentException => true,
        NotSupportedException => true,
        _ => false
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping RabbitMQ consumer service...");

        if (_channel is not null)
        {
            await _channel.CloseAsync(cancellationToken);
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.CloseAsync(cancellationToken);
            await _connection.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }
}
