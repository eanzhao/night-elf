namespace NightElf.Kernel.SmartContract;

public interface IStateCache
{
    bool TryGet(string key, out byte[]? value);

    byte[]? this[string key] { get; set; }
}
