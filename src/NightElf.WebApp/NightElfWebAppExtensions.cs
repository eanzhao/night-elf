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
        services.AddSingleton<TransactionSubmissionService>();
        services.AddSingleton<ContractDeploymentService>();
        services.AddSingleton<ChainSettlementEventBroker>();
        services.AddSingleton<ITransactionRelayService, NullTransactionRelayService>();
        services.AddSingleton<ITextTokenizer, WhitespaceTextTokenizer>();
        services.AddSingleton<ILocalModelInferenceInterceptor, LocalModelInferenceInterceptor>();
        services.AddSingleton<IRemoteApiUsageExtractor, OpenAiUsageExtractor>();
        return services;
    }

    public static IEndpointRouteBuilder MapNightElfWebApp(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGrpcService<NightElfNodeService>();
        endpoints.MapGrpcService<ChainSettlementService>();
        return endpoints;
    }
}
