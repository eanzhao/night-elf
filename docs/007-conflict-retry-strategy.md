# Phase 3 Conflict Retry Strategy

Issue: `#11`

## Goal

Keep optimistic execution as the first attempt, but stop treating every detected conflict as a direct serial full rerun. Conflicted transactions should be re-grouped into retry batches that can still execute in parallel where the read/write graph allows it.

## What Landed

### Conflict model

`NightElf.Kernel.Parallel` now includes:

- `ParallelTransaction` as the scheduler input
- `TransactionConflict` and `ConflictComponent` as the read/write conflict graph model
- `ConflictDetectionService` to classify write-write and read/write collisions

Read-read overlap is intentionally ignored.

### Retry planning

`ParallelExecutionPlanner` builds a multi-round execution plan:

1. round 0 chunks the incoming batch into optimistic groups
2. each group is analyzed after the optimistic attempt
3. conflict-free transactions commit immediately
4. conflicted components are colorized into retry groups with no intra-group conflicts
5. retry rounds continue until conflicts disappear or `MaxRetryRounds` is exhausted

This means NightElf now preserves the optimistic first pass while giving conflicted transactions a structured retry path instead of a blanket serial rerun.

### Timeout policy

The previous AElf-style fixed timeout approach is replaced by `ParallelExecutionRetryOptions`:

- `InitialGroupTimeout`
- `RetryTimeoutMultiplier`
- `ConflictTimeoutBias`
- `MaxRetryRounds`
- `OptimisticGroupSize`
- `MaxTransactionsPerRetryGroup`

`GetRoundTimeout(...)` scales the next retry timeout from both retry depth and previous-round conflict ratio, so timeout policy is explicit and adjustable rather than buried as a magic constant.

## Validation

`test/NightElf.Kernel.Parallel.Tests` covers:

- conflict detection vs. immediate commit-ready transactions
- regrouping a chain-conflict component into parallel retry groups
- retry exhaustion when `MaxRetryRounds = 0`
- adaptive timeout scaling
- a 16-transaction high-conflict batch that still produces retry groups instead of a serial full rerun

Repository validation command:

```bash
dotnet test NightElf.slnx
```
