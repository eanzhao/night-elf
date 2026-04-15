namespace NightElf.Kernel.Parallel;

public sealed class ConflictDetectionService
{
    public ConflictAnalysisResult Analyze(IReadOnlyList<ParallelTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);

        if (transactions.Count == 0)
        {
            return new ConflictAnalysisResult([], [], 0);
        }

        EnsureUniqueTransactions(transactions);

        var adjacency = transactions.ToDictionary(
            static transaction => transaction.TransactionId,
            static _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var conflicts = new List<TransactionConflict>();
        for (var i = 0; i < transactions.Count; i++)
        {
            for (var j = i + 1; j < transactions.Count; j++)
            {
                if (!TryGetConflictKind(transactions[i], transactions[j], out var kind))
                {
                    continue;
                }

                adjacency[transactions[i].TransactionId].Add(transactions[j].TransactionId);
                adjacency[transactions[j].TransactionId].Add(transactions[i].TransactionId);
                conflicts.Add(new TransactionConflict(
                    transactions[i].TransactionId,
                    transactions[j].TransactionId,
                    kind));
            }
        }

        var committedTransactions = transactions
            .Where(transaction => adjacency[transaction.TransactionId].Count == 0)
            .ToArray();

        var conflictComponents = BuildConflictComponents(transactions, adjacency, conflicts);
        return new ConflictAnalysisResult(committedTransactions, conflictComponents, transactions.Count);
    }

    private static IReadOnlyList<ConflictComponent> BuildConflictComponents(
        IReadOnlyList<ParallelTransaction> transactions,
        IReadOnlyDictionary<string, HashSet<string>> adjacency,
        IReadOnlyList<TransactionConflict> conflicts)
    {
        var byId = transactions.ToDictionary(
            static transaction => transaction.TransactionId,
            static transaction => transaction,
            StringComparer.Ordinal);

        var conflictKeys = conflicts.ToLookup(
            static conflict => CreateConflictKey(conflict.LeftTransactionId, conflict.RightTransactionId),
            static conflict => conflict,
            StringComparer.Ordinal);

        var components = new List<ConflictComponent>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var transaction in transactions)
        {
            if (adjacency[transaction.TransactionId].Count == 0 || !visited.Add(transaction.TransactionId))
            {
                continue;
            }

            var queue = new Queue<string>();
            var componentIds = new List<string>();
            queue.Enqueue(transaction.TransactionId);

            while (queue.Count > 0)
            {
                var currentId = queue.Dequeue();
                componentIds.Add(currentId);

                foreach (var neighborId in adjacency[currentId])
                {
                    if (visited.Add(neighborId))
                    {
                        queue.Enqueue(neighborId);
                    }
                }
            }

            var componentTransactions = componentIds
                .Select(id => byId[id])
                .ToArray();

            var componentIdSet = componentIds.ToHashSet(StringComparer.Ordinal);
            var componentConflicts = conflicts
                .Where(conflict =>
                    componentIdSet.Contains(conflict.LeftTransactionId) &&
                    componentIdSet.Contains(conflict.RightTransactionId))
                .ToArray();

            components.Add(new ConflictComponent(componentTransactions, componentConflicts));
        }

        return components;
    }

    private static void EnsureUniqueTransactions(IReadOnlyList<ParallelTransaction> transactions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transaction in transactions)
        {
            if (!seen.Add(transaction.TransactionId))
            {
                throw new InvalidOperationException(
                    $"Duplicate transaction id '{transaction.TransactionId}' cannot be analyzed.");
            }
        }
    }

    private static bool TryGetConflictKind(
        ParallelTransaction left,
        ParallelTransaction right,
        out TransactionConflictKind kind)
    {
        if (Overlaps(left.Resources.WriteKeys, right.Resources.WriteKeys))
        {
            kind = TransactionConflictKind.WriteWrite;
            return true;
        }

        if (Overlaps(left.Resources.WriteKeys, right.Resources.ReadKeys))
        {
            kind = TransactionConflictKind.WriteRead;
            return true;
        }

        if (Overlaps(left.Resources.ReadKeys, right.Resources.WriteKeys))
        {
            kind = TransactionConflictKind.ReadWrite;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool Overlaps(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return false;
        }

        var leftSet = left.ToHashSet(StringComparer.Ordinal);
        return right.Any(leftSet.Contains);
    }

    private static string CreateConflictKey(string leftTransactionId, string rightTransactionId)
    {
        return string.CompareOrdinal(leftTransactionId, rightTransactionId) <= 0
            ? $"{leftTransactionId}|{rightTransactionId}"
            : $"{rightTransactionId}|{leftTransactionId}";
    }
}
