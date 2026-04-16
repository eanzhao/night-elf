using Microsoft.AspNetCore.Server.Kestrel.Core;

using NightElf.Launcher;

var builder = WebApplication.CreateBuilder(args);
var launcherOptions = LauncherOptions.FromConfiguration(builder.Configuration);
launcherOptions.Validate();

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(
        launcherOptions.ApiPort,
        listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
});

builder.Services.AddGrpc();
builder.Services.AddGrpcHealthChecks().AddCheck("nightelf-node", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());
builder.Services.AddNightElfLauncher(builder.Configuration, launcherOptions);

var app = builder.Build();

app.MapGrpcHealthChecksService();

app.Logger.LogInformation(
    "NightElf launcher configured. API endpoint: http://127.0.0.1:{Port}",
    launcherOptions.ApiPort);

await app.RunAsync();
