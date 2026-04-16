using Microsoft.AspNetCore.Server.Kestrel.Core;

using NightElf.Launcher;
using NightElf.WebApp;

var app = Program.CreateApp(args);
await app.RunAsync();

public partial class Program
{
    public static WebApplication CreateApp(
        string[] args,
        Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configureBuilder?.Invoke(builder);

        var launcherOptions = LauncherOptions.FromConfiguration(builder.Configuration);
        launcherOptions.Validate();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(
                launcherOptions.ApiPort,
                listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
        });

        builder.Services.AddNightElfWebApp();
        builder.Services.AddGrpcHealthChecks().AddCheck("nightelf-node", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());
        builder.Services.AddNightElfLauncher(builder.Configuration, launcherOptions);

        var app = builder.Build();

        app.MapNightElfWebApp();
        app.MapGrpcHealthChecksService();
        app.MapGet("/health", static () => Results.Ok(new { status = "healthy" }))
            .ExcludeFromDescription();
        _ = app.Services.GetRequiredService<ChainSettlementEventBroker>();

        app.Logger.LogInformation(
            "NightElf launcher configured. API endpoint: http://127.0.0.1:{Port}",
            launcherOptions.ApiPort);

        return app;
    }
}
