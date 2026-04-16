using System.Buffers.Binary;
using System.Text;

using NightElf.Sdk.CSharp;

namespace NightElf.Contracts.System.TokenAotPrototype;

public readonly record struct MintInput(string Owner, long Amount) : IContractCodec<MintInput>
{
    public static MintInput Decode(ReadOnlySpan<byte> input)
    {
        var payload = Encoding.UTF8.GetString(input);
        var separatorIndex = payload.IndexOf('\n');
        if (separatorIndex <= 0 || separatorIndex == payload.Length - 1)
        {
            throw new FormatException("Expected a mint payload in '<owner>\\n<amount>' format.");
        }

        var owner = payload[..separatorIndex];
        if (!long.TryParse(payload[(separatorIndex + 1)..], out var amount))
        {
            throw new FormatException("The mint amount is invalid.");
        }

        return new MintInput(owner, amount);
    }

    public static byte[] Encode(MintInput value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Owner);
        return Encoding.UTF8.GetBytes($"{value.Owner}\n{value.Amount}");
    }
}

public readonly record struct BalanceInput(string Owner) : IContractCodec<BalanceInput>
{
    public static BalanceInput Decode(ReadOnlySpan<byte> input)
    {
        return new BalanceInput(Encoding.UTF8.GetString(input));
    }

    public static byte[] Encode(BalanceInput value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Owner);
        return Encoding.UTF8.GetBytes(value.Owner);
    }
}

public readonly record struct BalanceOutput(long Amount) : IContractCodec<BalanceOutput>
{
    public static BalanceOutput Decode(ReadOnlySpan<byte> input)
    {
        if (input.Length != sizeof(long))
        {
            throw new FormatException("Expected an 8-byte balance payload.");
        }

        return new BalanceOutput(BinaryPrimitives.ReadInt64LittleEndian(input));
    }

    public static byte[] Encode(BalanceOutput value)
    {
        var payload = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(payload, value.Amount);
        return payload;
    }
}
