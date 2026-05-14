using Onspay.SharedKernel;

namespace Onspay.Cqrs.Messaging;

public interface IDomainEventDispatcher
{
    Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default);
}
