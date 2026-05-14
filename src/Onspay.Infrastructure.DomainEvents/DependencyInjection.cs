using Microsoft.Extensions.DependencyInjection;
using Onspay.Cqrs.Messaging;
using Onspay.SharedKernel;

namespace Onspay.Infrastructure.DomainEvents;

public static class DependencyInjection
{
    public static IServiceCollection AddDomainEvents(this IServiceCollection services)
    {
        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        return services;
    }
}
