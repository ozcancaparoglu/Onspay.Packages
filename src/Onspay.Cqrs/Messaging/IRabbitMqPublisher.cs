namespace Onspay.Cqrs.Messaging;

public interface IRabbitMqPublisher
{
    Task PublishAsync<T>(
        T message,
        string messageType,
        string? messageId = null,
        string? correlationId = null,
        string? routingKey = null,
        CancellationToken cancellationToken = default) where T : class;
}