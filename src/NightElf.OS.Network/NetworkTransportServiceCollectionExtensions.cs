using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NightElf.OS.Network;

public static class NetworkTransportServiceCollectionExtensions
{
    public static IServiceCollection AddNetworkTransport(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(NetworkTransportOptions.SectionName);
        var quicSection = section.GetSection(nameof(NetworkTransportOptions.Quic));
        var options = new NetworkTransportOptions
        {
            RpcTransport = ParseTransport(
                section[nameof(NetworkTransportOptions.RpcTransport)],
                NetworkTransportKind.Grpc),
            BlockSyncTransport = ParseTransport(
                section[nameof(NetworkTransportOptions.BlockSyncTransport)],
                NetworkTransportKind.Grpc),
            TransactionBroadcastTransport = ParseTransport(
                section[nameof(NetworkTransportOptions.TransactionBroadcastTransport)],
                NetworkTransportKind.Grpc),
            Quic = new QuicTransportOptions
            {
                ServerName = quicSection[nameof(QuicTransportOptions.ServerName)] ?? "localhost",
                HandshakeTimeout = ParseTimeSpan(
                    quicSection[nameof(QuicTransportOptions.HandshakeTimeout)],
                    TimeSpan.FromSeconds(10)),
                IdleTimeout = ParseTimeSpan(
                    quicSection[nameof(QuicTransportOptions.IdleTimeout)],
                    TimeSpan.FromSeconds(30)),
                KeepAliveInterval = ParseTimeSpan(
                    quicSection[nameof(QuicTransportOptions.KeepAliveInterval)],
                    TimeSpan.FromSeconds(5)),
                ListenBacklog = ParseInt32(
                    quicSection[nameof(QuicTransportOptions.ListenBacklog)],
                    16),
                BlockSyncApplicationProtocol = quicSection[nameof(QuicTransportOptions.BlockSyncApplicationProtocol)] ?? "nightelf-sync/1.0",
                TransactionBroadcastApplicationProtocol = quicSection[nameof(QuicTransportOptions.TransactionBroadcastApplicationProtocol)] ?? "nightelf-tx/1.0"
            }
        };

        return services.AddNetworkTransport(options);
    }

    public static IServiceCollection AddNetworkTransport(
        this IServiceCollection services,
        NetworkTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton<INetworkMessageSink, NullNetworkMessageSink>();
        services.TryAddSingleton<IQuicCredentialProvider, EphemeralQuicCredentialProvider>();
        services.TryAddSingleton<GrpcCompatibilityTransport>();
        services.TryAddSingleton<QuicConnectionManager>();
        services.TryAddSingleton<QuicTransport>();
        services.TryAddSingleton<INetworkTransportCoordinator>(serviceProvider =>
            new NetworkTransportCoordinator(
                serviceProvider.GetRequiredService<NetworkTransportOptions>(),
                serviceProvider.GetRequiredService<INetworkMessageSink>(),
                serviceProvider.GetRequiredService<GrpcCompatibilityTransport>(),
                serviceProvider.GetRequiredService<QuicTransport>()));

        return services;
    }

    private static NetworkTransportKind ParseTransport(string? value, NetworkTransportKind defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (Enum.TryParse<NetworkTransportKind>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Unsupported network transport '{value}'.");
    }

    private static TimeSpan ParseTimeSpan(string? value, TimeSpan defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (TimeSpan.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Invalid TimeSpan value '{value}' in NightElf network configuration.");
    }

    private static int ParseInt32(string? value, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (int.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Invalid integer value '{value}' in NightElf network configuration.");
    }

    private sealed class NullNetworkMessageSink : INetworkMessageSink
    {
        public Task HandleAsync(NetworkMessageDelivery delivery, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(delivery);
            return Task.CompletedTask;
        }
    }
}
