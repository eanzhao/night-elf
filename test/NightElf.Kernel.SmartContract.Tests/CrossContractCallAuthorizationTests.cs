using System.Text;

using NightElf.Sdk.CSharp;

namespace NightElf.Kernel.SmartContract.Tests;

public sealed class CrossContractCallAuthorizationTests
{
    [Fact]
    public void Dynamic_Contract_Should_Allow_Cross_Call_When_Target_Is_In_Treaty_Permission_Matrix()
    {
        var checker = new FakePermissionGrantChecker();
        var harness = new ContractCallHarness(checker);
        harness.Register(
            "caller",
            new CallerContract { TargetAddress = "target", TargetMethodName = "Allowed" },
            contractName: "CallerContract",
            isDynamicContract: true,
            owningTreatyId: "treaty-x");
        harness.Register(
            "target",
            new ReturnContract("allowed"),
            contractName: "TargetContract",
            isDynamicContract: true,
            owningTreatyId: "treaty-y");
        checker.Allow("treaty-x", "agent-1", "TargetContract", "Allowed");

        var result = harness.Execute("caller", "Invoke", isDynamicContract: true, callerTreatyId: "treaty-x");

        Assert.Equal("allowed", Encoding.UTF8.GetString(result));
        Assert.Empty(harness.DeniedEvents);
    }

    [Fact]
    public void Dynamic_Contract_Should_Deny_Cross_Call_When_Target_Is_Not_In_Treaty_Permission_Matrix()
    {
        var harness = new ContractCallHarness(new FakePermissionGrantChecker());
        harness.Register(
            "caller",
            new CallerContract { TargetAddress = "target", TargetMethodName = "Denied" },
            contractName: "CallerContract",
            isDynamicContract: true,
            owningTreatyId: "treaty-x");
        harness.Register(
            "target",
            new ReturnContract("denied"),
            contractName: "TargetContract",
            isDynamicContract: true,
            owningTreatyId: "treaty-y");

        var exception = Assert.Throws<CrossContractCallDeniedException>(
            () => harness.Execute("caller", "Invoke", isDynamicContract: true, callerTreatyId: "treaty-x"));

        Assert.Contains("TargetContract.Denied", exception.Message, StringComparison.Ordinal);
        Assert.Single(harness.DeniedEvents);
        Assert.Equal("target", harness.DeniedEvents[0].TargetContractAddress);
    }

    [Fact]
    public void Dynamic_Contract_Without_Treaty_Should_Deny_All_Cross_Calls()
    {
        var harness = new ContractCallHarness(new FakePermissionGrantChecker());
        harness.Register(
            "caller",
            new CallerContract { TargetAddress = "agent-session", TargetMethodName = "OpenSession" },
            contractName: "CallerContract",
            isDynamicContract: true,
            owningTreatyId: null);
        harness.Register(
            "agent-session",
            new ReturnContract("system"),
            contractName: "AgentSession",
            isSystemContract: true);

        var exception = Assert.Throws<CrossContractCallDeniedException>(
            () => harness.Execute("caller", "Invoke", isDynamicContract: true, callerTreatyId: null));

        Assert.Contains("not associated with a treaty", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(harness.DeniedEvents);
    }

    [Fact]
    public void Dynamic_Contract_Should_Always_Be_Able_To_Call_System_Contracts()
    {
        var harness = new ContractCallHarness(new FakePermissionGrantChecker());
        harness.Register(
            "caller",
            new CallerContract { TargetAddress = "agent-session", TargetMethodName = "OpenSession" },
            contractName: "CallerContract",
            isDynamicContract: true,
            owningTreatyId: "treaty-x");
        harness.Register(
            "agent-session",
            new ReturnContract("system"),
            contractName: "AgentSession",
            isSystemContract: true);

        var result = harness.Execute("caller", "Invoke", isDynamicContract: true, callerTreatyId: "treaty-x");

        Assert.Equal("system", Encoding.UTF8.GetString(result));
        Assert.Empty(harness.DeniedEvents);
    }

    private sealed class ContractCallHarness
    {
        private readonly SmartContractExecutor _executor = new();
        private readonly IContractCallPermissionGrantChecker _checker;
        private readonly Dictionary<string, RegisteredContract> _contracts = new(StringComparer.OrdinalIgnoreCase);

        public ContractCallHarness(IContractCallPermissionGrantChecker checker)
        {
            _checker = checker;
        }

        public List<CrossContractCallDeniedEvent> DeniedEvents { get; } = [];

        public void Register(
            string address,
            CSharpSmartContract contract,
            string contractName,
            bool isDynamicContract = false,
            bool isSystemContract = false,
            string? owningTreatyId = null)
        {
            _contracts[address] = new RegisteredContract(
                contract,
                new ContractCallTargetInfo(
                    address,
                    contractName,
                    isDynamicContract,
                    isSystemContract,
                    owningTreatyId));
        }

        public byte[] Execute(
            string address,
            string methodName,
            bool isDynamicContract,
            string? callerTreatyId)
        {
            var registered = _contracts[address];
            return Execute(
                registered.Contract,
                new ContractInvocation(methodName, []),
                transactionId: "tx-1",
                senderAddress: "agent-1",
                contractAddress: address,
                isDynamicContract,
                callerTreatyId,
                timestamp: new DateTimeOffset(2026, 4, 17, 12, 0, 0, TimeSpan.Zero));
        }

        private byte[] Execute(
            CSharpSmartContract contract,
            ContractInvocation invocation,
            string transactionId,
            string senderAddress,
            string contractAddress,
            bool isDynamicContract,
            string? callerTreatyId,
            DateTimeOffset timestamp)
        {
            var stateProvider = new TestStateProvider();
            ContractExecutionContext? executionContext = null;
            var callHandler = new AuthorizingContractCallHandler(
                () => executionContext ?? throw new InvalidOperationException("Missing execution context."),
                targetAddress => _contracts.TryGetValue(targetAddress, out var registered) ? registered.Target : null,
                Dispatch,
                _checker,
                DeniedEvents.Add);

            executionContext = new ContractExecutionContext(
                new ContractStateContext(stateProvider),
                new ContractCallContext(callHandler),
                new ContractCryptoContext(new TestCryptoProvider()),
                new ContractIdentityContext(new TestIdentityProvider()),
                transactionId,
                senderAddress,
                contractAddress,
                blockHeight: 9,
                blockHash: "block-9",
                timestamp,
                transactionIndex: 3,
                isDynamicContract,
                callerTreatyId);

            return _executor.Execute(contract, invocation, executionContext);
        }

        private byte[] Dispatch(ContractCallDispatchRequest request)
        {
            var registered = _contracts[request.Target.ContractAddress];
            return Execute(
                registered.Contract,
                request.Invocation,
                request.CallerContext.TransactionId,
                request.CallerContext.SenderAddress,
                request.Target.ContractAddress,
                request.Target.IsDynamicContract,
                request.EffectiveCallerTreatyId,
                request.CallerContext.Timestamp);
        }

        private sealed record RegisteredContract(
            CSharpSmartContract Contract,
            ContractCallTargetInfo Target);
    }

    private sealed class CallerContract : CSharpSmartContract
    {
        public string TargetAddress { get; init; } = string.Empty;

        public string TargetMethodName { get; init; } = string.Empty;

        protected override byte[] DispatchCore(string methodName, ReadOnlyMemory<byte> input)
        {
            return methodName switch
            {
                "Invoke" => CallContext.Call(TargetAddress, new ContractInvocation(TargetMethodName, [])),
                _ => throw new ContractMethodNotFoundException(typeof(CallerContract), methodName)
            };
        }
    }

    private sealed class ReturnContract(string value) : CSharpSmartContract
    {
        protected override byte[] DispatchCore(string methodName, ReadOnlyMemory<byte> input)
        {
            return Encoding.UTF8.GetBytes(value);
        }
    }

    private sealed class FakePermissionGrantChecker : IContractCallPermissionGrantChecker
    {
        private readonly HashSet<string> _allowedCalls = new(StringComparer.Ordinal);

        public void Allow(
            string treatyId,
            string agentAddress,
            string contractNameOrAddress,
            string methodName)
        {
            _allowedCalls.Add(CreateKey(treatyId, agentAddress, contractNameOrAddress, methodName));
        }

        public bool IsAllowed(
            string treatyId,
            string agentAddress,
            ContractCallTargetInfo target,
            ContractInvocation invocation)
        {
            return _allowedCalls.Contains(CreateKey(treatyId, agentAddress, target.ContractName, invocation.MethodName)) ||
                   _allowedCalls.Contains(CreateKey(treatyId, agentAddress, target.ContractAddress, invocation.MethodName));
        }

        private static string CreateKey(
            string treatyId,
            string agentAddress,
            string contractNameOrAddress,
            string methodName)
        {
            return $"{treatyId}|{agentAddress}|{contractNameOrAddress}|{methodName}";
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

    private sealed class TestCryptoProvider : IContractCryptoProvider
    {
        public byte[] Hash(ReadOnlySpan<byte> input)
        {
            return Encoding.UTF8.GetBytes($"hash:{Encoding.UTF8.GetString(input)}");
        }

        public bool VerifySignature(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, string publicKey)
        {
            return true;
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
