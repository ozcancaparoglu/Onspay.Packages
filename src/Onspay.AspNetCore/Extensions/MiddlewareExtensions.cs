using Microsoft.AspNetCore.Builder;
using Onspay.AspNetCore.Middleware;

namespace Onspay.AspNetCore.Extensions;

public static class MiddlewareExtensions
{
    public static IApplicationBuilder UseRequestContextLogging(this IApplicationBuilder app)
    {
        app.UseMiddleware<RequestContextLoggingMiddleware>();
        return app;
    }
}
