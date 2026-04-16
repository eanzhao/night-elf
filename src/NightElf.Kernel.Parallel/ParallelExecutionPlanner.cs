namespace NightElf.Kernel.Parallel;

public sealed class ParallelExecutionPlanner
{
    private readonly ConflictDetectionService _conflictDetectionService;
    private readonly ParallelExecutionRetryOptions _options;

    public ParallelExecutionPlanner(
        ParallelExecutionRetryOptions? options = null,
        ConflictDetectionService? conflictDetectionService = null)
    {
        _options = options ?? new ParallelExecutionRetryOptions();
        _conflictDetectionService = conflictDetectionService ?? new ConflictDetectionService();
    }

    public ParallelExecutionPlan BuildPlan(IReadOnlyList<ParallelTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);

        _options.Validate();
        if (transactions.Count == 0)
        {
            return new ParallelExecutionPlan([], [], [], retryExhausted: false);
        }

        EnsureUniqueTransactions(transactions);

        var rounds = new List<ParallelExecutionRoundPlan>();
        var committedTransactions = new List<ParallelTransaction>();
        var originalOrder = transactions
            .Select((transaction, index) => new KeyValuePair<string, int>(transaction.TransactionId, index))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

        var currentGroups = CreateOptimisticGroups(transactions);
        var previousConflictRatio = 0.0d;
        var retryExhausted = false;
        IReadOnlyList<ConflictComponent> remainingConflictComponents = [];

        for (var roundNumber = 0; currentGroups.Count > 0; roundNumber++)
        {
            var timeout = _options.GetRoundTimeout(roundNumber, previousConflictRatio);
            var kind = roundNumber == 0
                ? ParallelExecutionGroupKind.Optimistic
                : ParallelExecutionGroupKind.Retry;

            var groupPlans = new List<ParallelExecutionGroupPlan>(currentGroups.Count);
            var roundCommittedTransactions = new List<ParallelTransaction>();
            var roundConflictComponents = new List<ConflictComponent>();
            var roundTransactionCount = 0;

            for (var groupIndex = 0; groupIndex < currentGroups.Count; groupIndex++)
            {
                var groupTransactions = currentGroups[groupIndex];
                roundTransactionCount += groupTransactions.Count;
                groupPlans.Add(new ParallelExecutionGroupPlan(
                    groupId: $"r{roundNumber}-g{groupIndex}",
                    roundNumber,
                    kind,
                    timeout,
                    groupTransactions));

                var analysis = _conflictDetectionService.Analyze(groupTransactions);
                roundCommittedTransactions.AddRange(analysis.CommittedTransactions);
                roundConflictComponents.AddRange(analysis.ConflictComponents);
            }

            roundCommittedTransactions = roundCommittedTransactions
                .OrderBy(transaction => originalOrder[transaction.TransactionId])
                .ToList();

            committedTransactions.AddRange(roundCommittedTransactions);

            var roundConflictTransactionCount = roundConflictComponents.Sum(static component => component.Transactions.Count);
            var roundConflictRatio = roundTransactionCount == 0
                ? 0.0d
                : (double)roundConflictTransactionCount / roundTransactionCount;

            rounds.Add(new ParallelExecutionRoundPlan(
                roundNumber,
                timeout,
                groupPlans,
                roundCommittedTransactions,
                roundConflictComponents,
                roundConflictRatio));

            if (roundConflictComponents.Count == 0)
            {
                remainingConflictComponents = [];
                retryExhausted = false;
                break;
            }

            if (roundNumber >= _options.MaxRetryRounds)
            {
                remainingConflictComponents = roundConflictComponents;
                retryExhausted = true;
                break;
            }

            currentGroups = CreateRetryGroups(roundConflictComponents);
            previousConflictRatio = roundConflictRatio;
        }

        return new ParallelExecutionPlan(
            rounds,
            committedTransactions.OrderBy(transaction => originalOrder[transaction.TransactionId]).ToArray(),
            remainingConflictComponents,
            retryExhausted);
    }

    private List<IReadOnlyList<ParallelTransaction>> CreateOptimisticGroups(IReadOnlyList<ParallelTransaction> transactions)
    {
        var groups = new List<IReadOnlyList<ParallelTransaction>>();
        for (var index = 0; index < transactions.Count; index += _options.OptimisticGroupSize)
        {
            var count = Math.Min(_options.OptimisticGroupSize, transactions.Count - index);
            var group = new ParallelTransaction[count];
            for (var i = 0; i < count; i++)
            {
                group[i] = transactions[index + i];
            }
            groups.Add(group);
        }

        return groups;
    }

    private List<IReadOnlyList<ParallelTransaction>> CreateRetryGroups(IReadOnlyList<ConflictComponent> conflictComponents)
    {
        var retryGroups = new List<IReadOnlyList<ParallelTransaction>>();
        foreach (var component in conflictComponents)
        {
            retryGroups.AddRange(ColorizeComponent(component));
        }

        return retryGroups;
    }

    private IEnumerable<IReadOnlyList<ParallelTransaction>> ColorizeComponent(ConflictComponent component)
    {
        var degrees = component.Conflicts
            .SelectMany(static conflict => new[]
            {
                conflict.LeftTransactionId,
                conflict.RightTransactionId
            })
            .GroupBy(static transactionId => transactionId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

        var conflictsByTransaction = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var transaction in component.Transactions)
        {
            conflictsByTransaction[transaction.TransactionId] = new HashSet<string>(StringComparer.Ordinal);
        }

        foreach (var conflict in component.Conflicts)
        {
            conflictsByTransaction[conflict.LeftTransactionId].Add(conflict.RightTransactionId);
            conflictsByTransaction[conflict.RightTransactionId].Add(conflict.LeftTransactionId);
        }

        var orderedTransactions = component.Transactions
            .OrderByDescending(transaction => degrees.GetValueOrDefault(transaction.TransactionId, 0))
            .ThenBy(transaction => transaction.TransactionId, StringComparer.Ordinal)
            .ToArray();

        var groups = new List<List<ParallelTransaction>>();
        foreach (var transaction in orderedTransactions)
        {
            var placed = false;
            foreach (var group in groups)
            {
                if (group.Count >= _options.MaxTransactionsPerRetryGroup)
                {
                    continue;
                }

                if (group.Any(existing => conflictsByTransaction[transaction.TransactionId].Contains(existing.TransactionId)))
                {
                    continue;
                }

                group.Add(transaction);
                placed = true;
                break;
            }

            if (!placed)
            {
                groups.Add([transaction]);
            }
        }

        return groups;
    }

    private static void EnsureUniqueTransactions(IReadOnlyList<ParallelTransaction> transactions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transaction in transactions)
        {
            if (!seen.Add(transaction.TransactionId))
            {
                throw new InvalidOperationException(
                    $"Duplicate transaction id '{transaction.TransactionId}' cannot be scheduled.");
            }
        }
    }
}
