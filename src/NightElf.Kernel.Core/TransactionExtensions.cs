using System.Security.Cryptography;

using Google.Protobuf;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

using NightElf.Kernel.Core.Protobuf;

namespace NightElf.Kernel.Core;

public static class TransactionExtensions
{
    public const int Ed25519PublicKeyLength = 32;
    public const int Ed25519SignatureLength = 64;
    public const int RefBlockPrefixLength = 4;

    public static byte[] GetUnsignedPayload(this Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var clone = transaction.Clone();
        clone.Signature = ByteString.Empty;
        return clone.ToByteArray();
    }

    public static byte[] GetSigningHash(this Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return SHA256.HashData(transaction.GetUnsignedPayload());
    }

    public static string GetTransactionId(this Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return Convert.ToHexString(SHA256.HashData(transaction.ToByteArray()));
    }

    public static Hash GetTransactionIdHash(this Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return new Hash
        {
            Value = ByteString.CopyFrom(SHA256.HashData(transaction.ToByteArray()))
        };
    }

    public static ByteString GetRefBlockPrefix(this string blockHashHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blockHashHex);

        var hashBytes = Convert.FromHexString(blockHashHex);
        if (hashBytes.Length < RefBlockPrefixLength)
        {
            throw new InvalidOperationException(
                $"Block hash '{blockHashHex}' must be at least {RefBlockPrefixLength} bytes long.");
        }

        return ByteString.CopyFrom(hashBytes.AsSpan(0, RefBlockPrefixLength).ToArray());
    }

    public static bool MatchesRefBlock(this Transaction transaction, BlockReference blockReference)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(blockReference);

        return transaction.RefBlockNumber == blockReference.Height &&
               transaction.RefBlockPrefix.Span.SequenceEqual(blockReference.Hash.GetRefBlockPrefix().Span);
    }

    public static bool VerifyEd25519Signature(this Transaction transaction, out string? error)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (!VerifyCoreFields(transaction, out error))
        {
            return false;
        }

        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(transaction.From.Value.ToByteArray(), 0));
            var signingHash = transaction.GetSigningHash();
            verifier.BlockUpdate(signingHash, 0, signingHash.Length);

            var verified = verifier.VerifySignature(transaction.Signature.ToByteArray());

            error = verified ? null : "Ed25519 signature verification failed.";
            return verified;
        }
        catch (Exception exception)
        {
            error = $"Ed25519 signature verification failed: {exception.Message}";
            return false;
        }
    }

    public static bool VerifyCoreFields(this Transaction transaction, out string? error)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (transaction.From is null || transaction.From.Value.IsEmpty)
        {
            error = "Transaction 'from' address must not be empty.";
            return false;
        }

        if (transaction.From.Value.Length != Ed25519PublicKeyLength)
        {
            error = $"Transaction 'from' address must contain a {Ed25519PublicKeyLength}-byte Ed25519 public key in Phase 1.";
            return false;
        }

        if (transaction.To is null || transaction.To.Value.IsEmpty)
        {
            error = "Transaction 'to' address must not be empty.";
            return false;
        }

        if (transaction.RefBlockNumber <= 0)
        {
            error = "Transaction ref block number must be greater than zero.";
            return false;
        }

        if (transaction.RefBlockPrefix.Length != RefBlockPrefixLength)
        {
            error = $"Transaction ref block prefix must be {RefBlockPrefixLength} bytes long.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(transaction.MethodName))
        {
            error = "Transaction method name must not be empty.";
            return false;
        }

        if (transaction.Signature.Length != Ed25519SignatureLength)
        {
            error = $"Transaction signature must be {Ed25519SignatureLength} bytes long for Ed25519.";
            return false;
        }

        error = null;
        return true;
    }
}
