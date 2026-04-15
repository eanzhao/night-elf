namespace NightElf.Sdk.CSharp;

public interface IContractCallHandler
{
    byte[] Call(string contractAddress, ContractInvocation invocation);

    void SendInline(string contractAddress, ContractInvocation invocation);
}
