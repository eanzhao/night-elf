using Google.Protobuf;

using NightElf.Sdk.CSharp;

namespace NightElf.Contracts.System.Treaty.Protobuf;

public sealed partial class TreatyParticipant : IContractCodec<TreatyParticipant>
{
    public static TreatyParticipant Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(TreatyParticipant value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class RoleBudget : IContractCodec<RoleBudget>
{
    public static RoleBudget Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(RoleBudget value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class PermissionGrant : IContractCodec<PermissionGrant>
{
    public static PermissionGrant Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(PermissionGrant value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class TreatyPermissionMatrix : IContractCodec<TreatyPermissionMatrix>
{
    public static TreatyPermissionMatrix Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(TreatyPermissionMatrix value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class TreatySpec : IContractCodec<TreatySpec>
{
    public static TreatySpec Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(TreatySpec value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class CreateTreatyInput : IContractCodec<CreateTreatyInput>
{
    public static CreateTreatyInput Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(CreateTreatyInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class JoinTreatyInput : IContractCodec<JoinTreatyInput>
{
    public static JoinTreatyInput Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(JoinTreatyInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class TreatyActivity : IContractCodec<TreatyActivity>
{
    public static TreatyActivity Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(TreatyActivity value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class RecordActivityInput : IContractCodec<RecordActivityInput>
{
    public static RecordActivityInput Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(RecordActivityInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class ChallengeStepInput : IContractCodec<ChallengeStepInput>
{
    public static ChallengeStepInput Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(ChallengeStepInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class TriggerKillSwitchInput : IContractCodec<TriggerKillSwitchInput>
{
    public static TriggerKillSwitchInput Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(TriggerKillSwitchInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class FinalizeTreatyInput : IContractCodec<FinalizeTreatyInput>
{
    public static FinalizeTreatyInput Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(FinalizeTreatyInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class GetTreatyInput : IContractCodec<GetTreatyInput>
{
    public static GetTreatyInput Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(GetTreatyInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class ParticipantState : IContractCodec<ParticipantState>
{
    public static ParticipantState Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(ParticipantState value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class TreatySessionBinding : IContractCodec<TreatySessionBinding>
{
    public static TreatySessionBinding Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(TreatySessionBinding value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class TreatySettlement : IContractCodec<TreatySettlement>
{
    public static TreatySettlement Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(TreatySettlement value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class TreatyState : IContractCodec<TreatyState>
{
    public static TreatyState Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(TreatyState value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class TreatyRecordedActivity : IContractCodec<TreatyRecordedActivity>
{
    public static TreatyRecordedActivity Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(TreatyRecordedActivity value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class TreatyChallengeRecord : IContractCodec<TreatyChallengeRecord>
{
    public static TreatyChallengeRecord Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(TreatyChallengeRecord value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class TreatyCreated : IContractCodec<TreatyCreated>
{
    public static TreatyCreated Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(TreatyCreated value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class TreatyJoined : IContractCodec<TreatyJoined>
{
    public static TreatyJoined Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(TreatyJoined value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class StepChallenged : IContractCodec<StepChallenged>
{
    public static StepChallenged Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(StepChallenged value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class KillSwitchTriggered : IContractCodec<KillSwitchTriggered>
{
    public static KillSwitchTriggered Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(KillSwitchTriggered value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class TreatyFinalized : IContractCodec<TreatyFinalized>
{
    public static TreatyFinalized Decode(ReadOnlySpan<byte> input) => Parser.ParseFrom(input.ToArray());

    public static byte[] Encode(TreatyFinalized value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}
