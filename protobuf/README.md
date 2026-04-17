# protobuf

Trimmed protocol buffer definitions inherited from AElf.

Current contents:
- `aelf/core.proto`: minimal base wire types (`Address`, `Hash`, `MerklePath`)
- `aelf/transaction.proto`: Phase 1 transaction messages (`Transaction`, `TransactionResult`)
- `aelf/block.proto`: Phase 1 block messages (`Block`, `BlockHeader`, `BlockBody`)

These files intentionally keep AElf field numbers and wire-compatible shapes, but use the
NightElf C# namespace so generated code can live inside `NightElf.Kernel.Core`.
