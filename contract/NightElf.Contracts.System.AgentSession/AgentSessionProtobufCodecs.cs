using Google.Protobuf;

using NightElf.Sdk.CSharp;

namespace NightElf.Contracts.System.AgentSession.Protobuf;

public sealed partial class OpenSessionInput : IContractCodec<OpenSessionInput>
{
    public static OpenSessionInput Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(OpenSessionInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class RecordStepInput : IContractCodec<RecordStepInput>
{
    public static RecordStepInput Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(RecordStepInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class FinalizeSessionInput : IContractCodec<FinalizeSessionInput>
{
    public static FinalizeSessionInput Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(FinalizeSessionInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class SessionState : IContractCodec<SessionState>
{
    public static SessionState Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(SessionState value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class SessionOpened : IContractCodec<SessionOpened>
{
    public static SessionOpened Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(SessionOpened value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class StepRecorded : IContractCodec<StepRecorded>
{
    public static StepRecorded Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(StepRecorded value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}

public sealed partial class SessionFinalized : IContractCodec<SessionFinalized>
{
    public static SessionFinalized Decode(ReadOnlySpan<byte> input)
    {
        return Parser.ParseFrom(input.ToArray());
    }

    public static byte[] Encode(SessionFinalized value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToByteArray();
    }
}
