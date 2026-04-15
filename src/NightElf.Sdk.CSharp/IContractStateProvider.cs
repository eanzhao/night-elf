namespace NightElf.Sdk.CSharp;

public interface IContractStateProvider
{
    byte[]? GetState(string key);

    void SetState(string key, byte[] value);

    void DeleteState(string key);

    bool StateExists(string key);
}
