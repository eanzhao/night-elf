using System.Text;

namespace NightElf.OS.Network;

internal static class NetworkEnvelopeSerializer
{
    private const int CurrentVersion = 1;

    private static readonly Encoding TextEncoding = Encoding.UTF8;

    public static byte[] Serialize(NetworkEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        envelope.Validate();

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, TextEncoding, leaveOpen: true);

        writer.Write(CurrentVersion);
        writer.Write((byte)envelope.Scenario);
        writer.Write(envelope.MessageId);
        writer.Write(envelope.SourceNodeId);
        writer.Write(envelope.CreatedAtUtc.UtcTicks);
        writer.Write(envelope.Payload.Length);
        writer.Write(envelope.Payload);
        writer.Flush();

        return stream.ToArray();
    }

    public static NetworkEnvelope Deserialize(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, TextEncoding, leaveOpen: false);

        var version = reader.ReadInt32();

        if (version != CurrentVersion)
        {
            throw new InvalidOperationException($"Unsupported network envelope version '{version}'.");
        }

        var scenarioValue = reader.ReadByte();

        if (!Enum.IsDefined(typeof(NetworkScenario), (int)scenarioValue))
        {
            throw new InvalidOperationException($"Unsupported network scenario value '{scenarioValue}'.");
        }

        var messageId = reader.ReadString();
        var sourceNodeId = reader.ReadString();
        var createdAtUtc = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var bodyLength = reader.ReadInt32();

        if (bodyLength < 0)
        {
            throw new InvalidOperationException($"Network envelope payload length '{bodyLength}' must not be negative.");
        }

        var body = reader.ReadBytes(bodyLength);

        if (body.Length != bodyLength)
        {
            throw new InvalidOperationException("Network envelope payload ended before the declared body length was reached.");
        }

        return new NetworkEnvelope
        {
            MessageId = messageId,
            SourceNodeId = sourceNodeId,
            Scenario = (NetworkScenario)scenarioValue,
            CreatedAtUtc = createdAtUtc,
            Payload = body
        };
    }
}
