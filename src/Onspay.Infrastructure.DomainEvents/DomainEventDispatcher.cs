using Microsoft.Extensions.DependencyInjection;
using Onspay.Cqrs.Messaging;
using Onspay.SharedKernel;

namespace Onspay.Infrastructure.DomainEvents;

public sealed class DomainEventDispatcher(IServiceProvider serviceProvider) : IDomainEventDispatcher
{
    public async Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(domainEvent.GetType());

        foreach (var handler in serviceProvider.GetServices(handlerType))
        {
            await (Task)handlerType
                .GetMethod(nameof(IDomainEventHandler<IDomainEvent>.Handle))!
                .Invoke(handler, [domainEvent, cancellationToken])!;
        }
    }
}
