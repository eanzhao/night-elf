using NightElf.Kernel.Core.Protobuf;

namespace NightElf.WebApp;

public interface ITransactionRelayService
{
    Task RelayAsync(
        Transaction transaction,
        CancellationToken cancellationToken = default);
}

public sealed class NullTransactionRelayService : ITransactionRelayService
{
    public Task RelayAsync(
        Transaction transaction,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(transaction);
        return Task.CompletedTask;
    }
}
