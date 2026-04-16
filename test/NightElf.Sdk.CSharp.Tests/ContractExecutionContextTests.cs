using System.Text;

using NightElf.Sdk.CSharp;

namespace NightElf.Sdk.CSharp.Tests;

public sealed class ContractExecutionContextTests
{
    [Fact]
    public void ContractStateContext_Should_Delegate_To_StateProvider()
    {
        var provider = new FakeStateProvider();
        var context = new ContractStateContext(provider);

        context.SetState("balance", Encoding.UTF8.GetBytes("42"));

        Assert.True(context.StateExists("balance"));
        Assert.Equal("42", Encoding.UTF8.GetString(context.GetState("balance")!));

        context.DeleteState("balance");

        Assert.False(context.StateExists("balance"));
    }

    [Fact]
    public void ContractCallContext_Should_Delegate_To_CallHandler()
    {
        var handler = new FakeCallHandler();
        var context = new ContractCallContext(handler);
        var callResult = context.Call("token", new ContractInvocation("Transfer", Encoding.UTF8.GetBytes("payload")));

        context.SendInline("token", new ContractInvocation("Approve", []));

        Assert.Equal("token:Transfer", Encoding.UTF8.GetString(callResult));
        Assert.Equal(["call:token:Transfer", "inline:token:Approve"], handler.Invocations);
    }

    [Fact]
    public void ContractCryptoContext_Should_Delegate_To_CryptoProvider()
    {
        var provider = new FakeCryptoProvider();
        var context = new ContractCryptoContext(provider);

        var hash = context.Hash(Encoding.UTF8.GetBytes("night"));
        var verified = context.VerifySignature(hash, Encoding.UTF8.GetBytes("sig"), "pub");
        var vrf = context.DeriveVrfProof(Encoding.UTF8.GetBytes("seed"));

        Assert.Equal("hash:night", Encoding.UTF8.GetString(hash));
        Assert.True(verified);
        Assert.Equal("vrf:seed", Encoding.UTF8.GetString(vrf));
    }

    [Fact]
    public void ContractIdentityContext_Should_Delegate_To_IdentityProvider()
    {
        var provider = new FakeIdentityProvider();
        var context = new ContractIdentityContext(provider);

        Assert.Equal("addr:user", context.GenerateAddress("user"));
        Assert.Equal("virtual:token:salt", context.GetVirtualAddress("token", "salt"));
    }

    [Fact]
    public void ContractExecutionContext_Should_Compose_Specialized_Contexts_And_Metadata()
    {
        var state = new ContractStateContext(new FakeStateProvider());
        var calls = new ContractCallContext(new FakeCallHandler());
        var crypto = new ContractCryptoContext(new FakeCryptoProvider());
        var identity = new ContractIdentityContext(new FakeIdentityProvider());
        var timestamp = new DateTimeOffset(2026, 4, 16, 12, 34, 56, TimeSpan.Zero);

        var context = new ContractExecutionContext(
            state,
            calls,
            crypto,
            identity,
            transactionId: "tx-1",
            senderAddress: "sender",
            currentContractAddress: "token",
            blockHeight: 123,
            blockHash: "block-hash",
            timestamp: timestamp,
            transactionIndex: 7);

        Assert.Same(state, context.State);
        Assert.Same(calls, context.Calls);
        Assert.Same(crypto, context.Crypto);
        Assert.Same(identity, context.Identity);
        Assert.Equal("tx-1", context.TransactionId);
        Assert.Equal("sender", context.SenderAddress);
        Assert.Equal("token", context.CurrentContractAddress);
        Assert.Equal(123, context.BlockHeight);
        Assert.Equal("block-hash", context.BlockHash);
        Assert.Equal(timestamp, context.Timestamp);
        Assert.Equal(7, context.TransactionIndex);
    }

    private sealed class FakeStateProvider : IContractStateProvider
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

    private sealed class FakeCallHandler : IContractCallHandler
    {
        public List<string> Invocations { get; } = [];

        public byte[] Call(string contractAddress, ContractInvocation invocation)
        {
            Invocations.Add($"call:{contractAddress}:{invocation.MethodName}");
            return Encoding.UTF8.GetBytes($"{contractAddress}:{invocation.MethodName}");
        }

        public void SendInline(string contractAddress, ContractInvocation invocation)
        {
            Invocations.Add($"inline:{contractAddress}:{invocation.MethodName}");
        }
    }

    private sealed class FakeCryptoProvider : IContractCryptoProvider
    {
        public byte[] Hash(ReadOnlySpan<byte> input)
        {
            return Encoding.UTF8.GetBytes($"hash:{Encoding.UTF8.GetString(input)}");
        }

        public bool VerifySignature(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, string publicKey)
        {
            return publicKey == "pub" && !data.IsEmpty && !signature.IsEmpty;
        }

        public byte[] DeriveVrfProof(ReadOnlySpan<byte> seed)
        {
            return Encoding.UTF8.GetBytes($"vrf:{Encoding.UTF8.GetString(seed)}");
        }
    }

    private sealed class FakeIdentityProvider : IContractIdentityProvider
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
