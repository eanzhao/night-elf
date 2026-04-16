# protobuf

Trimmed protocol buffer definitions inherited from AElf.

Current contents:
- `aelf/core.proto`: minimal core wire types used by transactions and block metadata
- `kernel.proto`: minimal block messages used by the NightElf kernel

These files intentionally keep AElf field numbers and wire-compatible shapes, but use the
NightElf C# namespace so generated code can live inside `NightElf.Kernel.Core`.
