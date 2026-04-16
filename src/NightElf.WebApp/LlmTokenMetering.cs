using System.Text.Json;

using NightElf.Contracts.System.AgentSession;
using NightElf.Contracts.System.AgentSession.Protobuf;

namespace NightElf.WebApp;

public sealed record TokenMeteringReading(
    long InputTokens,
    long OutputTokens,
    MeteringSource Source,
    int ConfidenceWeightBasisPoints)
{
    public long TotalTokens => checked(InputTokens + OutputTokens);

    public long WeightedTokens => Source.ApplyConfidenceWeight(TotalTokens);
}

public interface ITextTokenizer
{
    long CountTokens(string? text);
}

public sealed class WhitespaceTextTokenizer : ITextTokenizer
{
    public long CountTokens(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LongLength;
    }
}

public interface ILocalModelInferenceInterceptor
{
    TokenMeteringReading Intercept(string? prompt, string? completion);
}

public sealed class LocalModelInferenceInterceptor : ILocalModelInferenceInterceptor
{
    private readonly ITextTokenizer _tokenizer;

    public LocalModelInferenceInterceptor(ITextTokenizer tokenizer)
    {
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
    }

    public TokenMeteringReading Intercept(string? prompt, string? completion)
    {
        var inputTokens = _tokenizer.CountTokens(prompt);
        var outputTokens = _tokenizer.CountTokens(completion);
        return new TokenMeteringReading(
            inputTokens,
            outputTokens,
            MeteringSource.Verified,
            MeteringSource.Verified.GetConfidenceWeightBasisPoints());
    }
}

public interface IRemoteApiUsageExtractor
{
    TokenMeteringReading Extract(ReadOnlySpan<byte> responsePayload);
}

public sealed class OpenAiUsageExtractor : IRemoteApiUsageExtractor
{
    public TokenMeteringReading Extract(ReadOnlySpan<byte> responsePayload)
    {
        if (responsePayload.IsEmpty)
        {
            throw new InvalidOperationException("Remote API response payload must not be empty.");
        }

        using var document = JsonDocument.Parse(responsePayload.ToArray());
        if (!TryGetProperty(document.RootElement, "usage", out var usage))
        {
            throw new InvalidOperationException("Remote API response does not contain a usage object.");
        }

        var inputTokens = ReadRequiredTokenCount(usage, "prompt_tokens", "input_tokens");
        var outputTokens = ReadRequiredTokenCount(usage, "completion_tokens", "output_tokens");

        return new TokenMeteringReading(
            inputTokens,
            outputTokens,
            MeteringSource.SelfReported,
            MeteringSource.SelfReported.GetConfidenceWeightBasisPoints());
    }

    private static long ReadRequiredTokenCount(
        JsonElement usage,
        string primaryPropertyName,
        string secondaryPropertyName)
    {
        if (TryGetProperty(usage, primaryPropertyName, out var primaryValue))
        {
            return ReadInt64(primaryValue, primaryPropertyName);
        }

        if (TryGetProperty(usage, secondaryPropertyName, out var secondaryValue))
        {
            return ReadInt64(secondaryValue, secondaryPropertyName);
        }

        throw new InvalidOperationException(
            $"Remote API usage payload does not contain '{primaryPropertyName}' or '{secondaryPropertyName}'.");
    }

    private static long ReadInt64(JsonElement value, string propertyName)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var numericValue) => numericValue,
            JsonValueKind.String when long.TryParse(value.GetString(), out var stringValue) => stringValue,
            _ => throw new InvalidOperationException(
                $"Remote API usage field '{propertyName}' must be an integer number.")
        };
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        var pascalCaseName = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
        return element.TryGetProperty(pascalCaseName, out value);
    }
}
