# NightElf.Kernel.Parallel

Parallel execution primitives for NightElf.

Current scope:

- `ResourceExtractionService` consumes generated resource declarations
- `ConflictDetectionService` turns read/write sets into conflict components
- `ParallelExecutionPlanner` re-groups conflicted transactions for retry rounds
- contracts without generated resource metadata fall back to `ContractResourceSet.Empty`
- timeout and retry policy live in `ParallelExecutionRetryOptions`
