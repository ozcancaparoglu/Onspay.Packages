using Microsoft.Extensions.DependencyInjection;
using Onspay.AspNetCore.ExceptionHandling;

namespace Onspay.AspNetCore;

public static class DependencyInjection
{
    public static IServiceCollection AddPresentation(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();
        return services;
    }
}
