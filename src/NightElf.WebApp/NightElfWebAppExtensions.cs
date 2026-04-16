using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace NightElf.WebApp;

public static class NightElfWebAppExtensions
{
    public static IServiceCollection AddNightElfWebApp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddGrpc();
        return services;
    }

    public static IEndpointRouteBuilder MapNightElfWebApp(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGrpcService<NightElfNodeService>();
        return endpoints;
    }
}
