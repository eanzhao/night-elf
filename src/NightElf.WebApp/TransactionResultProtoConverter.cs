using NightElf.Kernel.Core;
using NightElf.Kernel.Core.Protobuf;
using NightElf.WebApp.Protobuf;
using ApiTransactionResult = NightElf.WebApp.Protobuf.TransactionResult;
using ChainTransactionResultStatus = NightElf.Kernel.Core.TransactionResultStatus;

namespace NightElf.WebApp;

public static class TransactionResultProtoConverter
{
    public static ApiTransactionResult ToProto(TransactionResultRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new ApiTransactionResult
        {
            TransactionId = record.TransactionId.ToProtoHash(),
            Status = record.Status switch
            {
                ChainTransactionResultStatus.Pending => TransactionExecutionStatus.Pending,
                ChainTransactionResultStatus.Mined => TransactionExecutionStatus.Mined,
                ChainTransactionResultStatus.Failed => TransactionExecutionStatus.Failed,
                ChainTransactionResultStatus.Rejected => TransactionExecutionStatus.Rejected,
                _ => TransactionExecutionStatus.Unspecified
            },
            Error = record.Error ?? string.Empty,
            BlockHeight = record.BlockHeight,
            BlockHash = string.IsNullOrWhiteSpace(record.BlockHash)
                ? new Hash()
                : record.BlockHash.ToProtoHash()
        };
    }

    public static ApiTransactionResult CreatePending(string transactionId, string? error = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);

        return new ApiTransactionResult
        {
            TransactionId = transactionId.ToProtoHash(),
            Status = TransactionExecutionStatus.Pending,
            Error = error ?? string.Empty
        };
    }

    public static ApiTransactionResult CreateRejected(string? transactionId, string? error)
    {
        return new ApiTransactionResult
        {
            TransactionId = string.IsNullOrWhiteSpace(transactionId)
                ? new Hash()
                : transactionId.ToProtoHash(),
            Status = TransactionExecutionStatus.Rejected,
            Error = error ?? string.Empty
        };
    }

    public static ApiTransactionResult Create(
        string transactionId,
        ChainTransactionResultStatus status,
        string? error = null,
        BlockReference? block = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);

        return new ApiTransactionResult
        {
            TransactionId = transactionId.ToProtoHash(),
            Status = status switch
            {
                ChainTransactionResultStatus.Pending => TransactionExecutionStatus.Pending,
                ChainTransactionResultStatus.Mined => TransactionExecutionStatus.Mined,
                ChainTransactionResultStatus.Failed => TransactionExecutionStatus.Failed,
                ChainTransactionResultStatus.Rejected => TransactionExecutionStatus.Rejected,
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
