using Microsoft.AspNetCore.Routing;

namespace Onspay.AspNetCore.Endpoints;

public interface IEndpoint
{
    void MapEndpoint(IEndpointRouteBuilder app);
}
