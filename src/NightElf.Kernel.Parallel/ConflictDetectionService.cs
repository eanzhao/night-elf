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

        var writeKeySets = transactions.ToDictionary(
            static t => t.TransactionId,
            static t => t.Resources.WriteKeys.Count > 0 ? t.Resources.WriteKeys.ToHashSet(StringComparer.Ordinal) : null,
            StringComparer.Ordinal);
        var readKeySets = transactions.ToDictionary(
            static t => t.TransactionId,
            static t => t.Resources.ReadKeys.Count > 0 ? t.Resources.ReadKeys.ToHashSet(StringComparer.Ordinal) : null,
            StringComparer.Ordinal);

        var conflicts = new List<TransactionConflict>();
        for (var i = 0; i < transactions.Count; i++)
        {
            for (var j = i + 1; j < transactions.Count; j++)
            {
                var left = transactions[i];
                var right = transactions[j];
                if (!TryGetConflictKind(
                        writeKeySets[left.TransactionId], readKeySets[left.TransactionId],
                        writeKeySets[right.TransactionId], readKeySets[right.TransactionId],
                        out var kind))
                {
                    continue;
                }

                adjacency[left.TransactionId].Add(right.TransactionId);
                adjacency[right.TransactionId].Add(left.TransactionId);
                conflicts.Add(new TransactionConflict(
                    left.TransactionId,
                    right.TransactionId,
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
        HashSet<string>? leftWriteSet,
        HashSet<string>? leftReadSet,
        HashSet<string>? rightWriteSet,
        HashSet<string>? rightReadSet,
        out TransactionConflictKind kind)
    {
        kind = default;
        var found = false;

        if (Overlaps(leftWriteSet, rightWriteSet))
        {
            kind = TransactionConflictKind.WriteWrite;
            return true; // WriteWrite is the worst, no need to check further
        }

        if (Overlaps(leftWriteSet, rightReadSet))
        {
            kind = TransactionConflictKind.WriteRead;
            found = true;
        }

        if (Overlaps(leftReadSet, rightWriteSet))
        {
            if (!found)
            {
                kind = TransactionConflictKind.ReadWrite;
            }
            found = true;
        }

        return found;
    }

    private static bool Overlaps(HashSet<string>? leftSet, HashSet<string>? rightSet)
    {
        if (leftSet is null || leftSet.Count == 0 || rightSet is null || rightSet.Count == 0)
        {
            return false;
        }

        return rightSet.Any(leftSet.Contains);
    }

    private static string CreateConflictKey(string leftTransactionId, string rightTransactionId)
    {
        return string.CompareOrdinal(leftTransactionId, rightTransactionId) <= 0
            ? $"{leftTransactionId}|{rightTransactionId}"
            : $"{rightTransactionId}|{leftTransactionId}";
    }
}
