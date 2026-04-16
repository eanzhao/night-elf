using Google.Protobuf;

using NightElf.Sdk.CSharp;

namespace NightElf.Contracts.System.SentinelRegistry.Protobuf;

public sealed partial class RegisterSentinelInput : IContractCodec<RegisterSentinelInput>
{
    public static RegisterSentinelInput Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(RegisterSentinelInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class ExitSentinelInput : IContractCodec<ExitSentinelInput>
{
    public static ExitSentinelInput Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(ExitSentinelInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class RecordComputationCreditInput : IContractCodec<RecordComputationCreditInput>
{
    public static RecordComputationCreditInput Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(RecordComputationCreditInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class AdvanceEpochInput : IContractCodec<AdvanceEpochInput>
{
    public static AdvanceEpochInput Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(AdvanceEpochInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class UpdateGovernanceInput : IContractCodec<UpdateGovernanceInput>
{
    public static UpdateGovernanceInput Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(UpdateGovernanceInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class GetSentinelInput : IContractCodec<GetSentinelInput>
{
    public static GetSentinelInput Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(GetSentinelInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class SentinelState : IContractCodec<SentinelState>
{
    public static SentinelState Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(SentinelState value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class EpochSnapshot : IContractCodec<EpochSnapshot>
{
    public static EpochSnapshot Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(EpochSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class GovernanceParameters : IContractCodec<GovernanceParameters>
{
    public static GovernanceParameters Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(GovernanceParameters value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class SentinelRegistered : IContractCodec<SentinelRegistered>
{
    public static SentinelRegistered Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(SentinelRegistered value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class SentinelExited : IContractCodec<SentinelExited>
{
    public static SentinelExited Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(SentinelExited value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class ComputationCreditRecorded : IContractCodec<ComputationCreditRecorded>
{
    public static ComputationCreditRecorded Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(ComputationCreditRecorded value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class EpochAdvanced : IContractCodec<EpochAdvanced>
{
    public static EpochAdvanced Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(EpochAdvanced value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}
