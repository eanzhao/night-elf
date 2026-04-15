namespace NightElf.Sdk.CSharp;

public sealed class ContractStateContext
{
    private readonly IContractStateProvider _provider;

    public ContractStateContext(IContractStateProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public byte[]? GetState(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return _provider.GetState(key);
    }

    public void SetState(string key, byte[] value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        _provider.SetState(key, value);
    }

    public void DeleteState(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _provider.DeleteState(key);
    }

    public bool StateExists(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return _provider.StateExists(key);
    }
}
