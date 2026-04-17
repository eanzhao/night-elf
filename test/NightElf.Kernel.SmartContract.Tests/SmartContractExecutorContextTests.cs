using System.Text;

using NightElf.Kernel.SmartContract;
using NightElf.Sdk.CSharp;

namespace NightElf.Kernel.SmartContract.Tests;

public sealed class SmartContractExecutorContextTests
{
    [Fact]
    public void Execute_Should_Expose_ExecutionContext_During_Dispatch_Only()
    {
        var contract = new ContextAwareContract();
        var executor = new SmartContractExecutor();
        var executionContext = CreateExecutionContext();

        var result = executor.Execute(
            contract,
            new ContractInvocation("Snapshot", []),
            executionContext);

        Assert.Equal(
            "tx-42|sender|token|321|17|block-321|77|True|treaty-9|addr:user|virtual:token:salt|hash:payload|vrf:seed|True|target:Call",
            Encoding.UTF8.GetString(result));

        var exception = Assert.Throws<InvalidOperationException>(
            () => contract.Dispatch("Snapshot", []));
        Assert.Contains("execution context", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ContractExecutionContext CreateExecutionContext()
    {
        return new ContractExecutionContext(
            new ContractStateContext(new TestStateProvider()),
            new ContractCallContext(new TestCallHandler()),
            new ContractCryptoContext(new TestCryptoProvider()),
            new ContractIdentityContext(new TestIdentityProvider()),
            transactionId: "tx-42",
            senderAddress: "sender",
            currentContractAddress: "token",
            blockHeight: 321,
            blockHash: "block-321",
            timestamp: new DateTimeOffset(2026, 4, 16, 18, 0, 0, TimeSpan.Zero),
            transactionIndex: 17,
            isDynamicContract: true,
            callerTreatyId: "treaty-9");
    }

    private sealed class ContextAwareContract : CSharpSmartContract
    {
        protected override byte[] DispatchCore(string methodName, ReadOnlyMemory<byte> input)
        {
            return methodName switch
            {
                "Snapshot" => Snapshot(),
                _ => throw new ContractMethodNotFoundException(typeof(ContextAwareContract), methodName)
            };
        }

        private byte[] Snapshot()
        {
            StateContext.SetState("counter", Encoding.UTF8.GetBytes("77"));

            var counter = Encoding.UTF8.GetString(StateContext.GetState("counter")!);
            var generated = IdentityContext.GenerateAddress("user");
            var virtualAddress = IdentityContext.GetVirtualAddress(ExecutionContext.CurrentContractAddress, "salt");
            var hash = Encoding.UTF8.GetString(CryptoContext.Hash(Encoding.UTF8.GetBytes("payload")));
            var vrf = Encoding.UTF8.GetString(CryptoContext.DeriveVrfProof(Encoding.UTF8.GetBytes("seed")));
            var verified = CryptoContext.VerifySignature(Encoding.UTF8.GetBytes("payload"), Encoding.UTF8.GetBytes("sig"), "pub");
            var callResult = Encoding.UTF8.GetString(CallContext.Call("target", new ContractInvocation("Call", [])));

            return Encoding.UTF8.GetBytes(
                $"{ExecutionContext.TransactionId}|{ExecutionContext.SenderAddress}|{ExecutionContext.CurrentContractAddress}|{ExecutionContext.BlockHeight}|{ExecutionContext.TransactionIndex}|{ExecutionContext.BlockHash}|{counter}|{ExecutionContext.IsDynamicContract}|{ExecutionContext.CallerTreatyId}|{generated}|{virtualAddress}|{hash}|{vrf}|{verified}|{callResult}");
        }
    }

    private sealed class TestStateProvider : IContractStateProvider
    {
        private readonly Dictionary<string, byte[]> _states = new(StringComparer.Ordinal);

        public byte[]? GetState(string key)
        {
            return _states.TryGetValue(key, out var value) ? value : null;
        }

        public void SetState(string key, byte[] value)
        {
            _states[key] = value;
        }

        public void DeleteState(string key)
        {
            _states.Remove(key);
        }

        public bool StateExists(string key)
        {
            return _states.ContainsKey(key);
        }
    }

    private sealed class TestCallHandler : IContractCallHandler
    {
        public byte[] Call(string contractAddress, ContractInvocation invocation)
        {
            return Encoding.UTF8.GetBytes($"{contractAddress}:{invocation.MethodName}");
        }

        public void SendInline(string contractAddress, ContractInvocation invocation)
        {
        }
    }

    private sealed class TestCryptoProvider : IContractCryptoProvider
    {
        public byte[] Hash(ReadOnlySpan<byte> input)
        {
            return Encoding.UTF8.GetBytes($"hash:{Encoding.UTF8.GetString(input)}");
        }

        public bool VerifySignature(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, string publicKey)
        {
            return publicKey == "pub";
        }

        public byte[] DeriveVrfProof(ReadOnlySpan<byte> seed)
        {
            return Encoding.UTF8.GetBytes($"vrf:{Encoding.UTF8.GetString(seed)}");
        }
    }

    private sealed class TestIdentityProvider : IContractIdentityProvider
    {
        public string GenerateAddress(string seed)
        {
            return $"addr:{seed}";
        }

        public string GetVirtualAddress(string contractAddress, string salt)
        {
            return $"virtual:{contractAddress}:{salt}";
        }
    }
}
