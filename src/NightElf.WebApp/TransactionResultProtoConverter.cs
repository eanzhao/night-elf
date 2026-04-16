using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.WebApp.Protobuf;

namespace NightElf.WebApp;

public static class TransactionResultProtoConverter
{
    public static TransactionResult ToProto(TransactionResultRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new TransactionResult
        {
            TransactionId = record.TransactionId.ToProtoHash(),
            Status = record.Status switch
            {
                TransactionResultStatus.Pending => TransactionExecutionStatus.Pending,
                TransactionResultStatus.Mined => TransactionExecutionStatus.Mined,
                TransactionResultStatus.Failed => TransactionExecutionStatus.Failed,
                TransactionResultStatus.Rejected => TransactionExecutionStatus.Rejected,
                _ => TransactionExecutionStatus.Unspecified
            },
            Error = record.Error ?? string.Empty,
            BlockHeight = record.BlockHeight,
            BlockHash = string.IsNullOrWhiteSpace(record.BlockHash)
                ? new Hash()
                : record.BlockHash.ToProtoHash()
        };
    }

    public static TransactionResult CreatePending(string transactionId, string? error = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);

        return new TransactionResult
        {
            TransactionId = transactionId.ToProtoHash(),
            Status = TransactionExecutionStatus.Pending,
            Error = error ?? string.Empty
        };
    }

    public static TransactionResult CreateRejected(string? transactionId, string? error)
    {
        return new TransactionResult
        {
            TransactionId = string.IsNullOrWhiteSpace(transactionId)
                ? new Hash()
                : transactionId.ToProtoHash(),
            Status = TransactionExecutionStatus.Rejected,
            Error = error ?? string.Empty
        };
    }

    public static TransactionResult Create(
        string transactionId,
        TransactionResultStatus status,
        string? error = null,
        BlockReference? block = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);

        return new TransactionResult
        {
            TransactionId = transactionId.ToProtoHash(),
            Status = status switch
            {
                TransactionResultStatus.Pending => TransactionExecutionStatus.Pending,
                TransactionResultStatus.Mined => TransactionExecutionStatus.Mined,
                TransactionResultStatus.Failed => TransactionExecutionStatus.Failed,
                TransactionResultStatus.Rejected => TransactionExecutionStatus.Rejected,
                _ => TransactionExecutionStatus.Unspecified
            },
            Error = error ?? string.Empty,
            BlockHeight = block?.Height ?? 0,
            BlockHash = string.IsNullOrWhiteSpace(block?.Hash)
                ? new Hash()
                : block.Hash.ToProtoHash()
        };
    }
}
