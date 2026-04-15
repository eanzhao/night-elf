namespace NightElf.Sdk.CSharp;

public sealed class ContractExecutionContext
{
    public ContractExecutionContext(
        ContractStateContext state,
        ContractCallContext calls,
        ContractCryptoContext crypto,
        ContractIdentityContext identity,
        string transactionId,
        string senderAddress,
        string currentContractAddress,
        long blockHeight,
        string blockHash,
        DateTimeOffset timestamp)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Calls = calls ?? throw new ArgumentNullException(nameof(calls));
        Crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));

        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentContractAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(blockHash);

        TransactionId = transactionId;
        SenderAddress = senderAddress;
        CurrentContractAddress = currentContractAddress;
        BlockHeight = blockHeight;
        BlockHash = blockHash;
        Timestamp = timestamp;
    }

    public ContractStateContext State { get; }

    public ContractCallContext Calls { get; }

    public ContractCryptoContext Crypto { get; }

    public ContractIdentityContext Identity { get; }

    public string TransactionId { get; }

    public string SenderAddress { get; }

    public string CurrentContractAddress { get; }

    public long BlockHeight { get; }

    public string BlockHash { get; }

    public DateTimeOffset Timestamp { get; }
}
