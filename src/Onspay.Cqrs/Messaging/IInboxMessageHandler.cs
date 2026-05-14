using Onspay.SharedKernel;

namespace Onspay.Cqrs.Messaging;

public interface IInboxMessageHandler
{
    string MessageType { get; }
    Task<Result> HandleAsync(string payload, CancellationToken cancellationToken);
}

public interface IInboxMessageHandler<in TMessage> : IInboxMessageHandler
    where TMessage : class
{
    Task<Result> HandleAsync(TMessage message, CancellationToken cancellationToken);
}
