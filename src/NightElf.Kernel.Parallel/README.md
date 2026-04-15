# NightElf.Kernel.Parallel

Parallel execution primitives for NightElf.

Current scope:

- `ResourceExtractionService` consumes generated resource declarations
- contracts without generated resource metadata fall back to `ContractResourceSet.Empty`
- module boundary for future MVCC grouping and conflict detection work
