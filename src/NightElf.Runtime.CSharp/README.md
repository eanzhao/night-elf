# NightElf.Runtime.CSharp

C# contract runtime sandbox for NightElf.

Current scope:

- `ContractSandbox : AssemblyLoadContext` with collectible unload semantics
- explicit assembly whitelist / shared-assembly policy
- minimal timeout execution service based on `CancellationTokenSource.CancelAfter`
- documentation boundary that IL patching is separate from sandbox isolation
