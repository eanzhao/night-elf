# Phase 4 Contract Sandbox via AssemblyLoadContext

Issue: `#13`

## Goal

Replace the old whitelist-plus-IL-patch sandbox model with a collectible `AssemblyLoadContext` boundary that can load, isolate, and unload contract assemblies while keeping timeout control explicit.

## What Landed

### Runtime sandbox

`NightElf.Runtime.CSharp` is now a real runtime project with:

- `ContractSandbox : AssemblyLoadContext`
- `ContractSandboxOptions`
- `ContractAssemblyNotAllowedException`
- `ContractSandboxExecutionService`
- `ContractSandboxTimeoutException`

The sandbox is created with `isCollectible: true`, so contracts can be loaded into an unloadable context instead of the default process-wide context.

### Assembly policy

The runtime now splits assemblies into two buckets:

- shared assemblies, such as `NightElf.Sdk.CSharp`, resolved from the default load context
- allowed assemblies, explicitly whitelisted and resolved from registered paths or the contract root directory

Any non-platform dependency outside the whitelist fails with `ContractAssemblyNotAllowedException`.

This keeps the sandbox boundary in the loader itself instead of relying on post-load patching.

### Timeout control

`ContractSandboxExecutionService` wraps execution with `CancellationTokenSource.CancelAfter(...)` and translates timeout-triggered cancellation into `ContractSandboxTimeoutException`.

This is the minimum verifiable timeout layer for the runtime. IL patching remains a separate concern and is still reserved for gas/accounting work, not for sandbox isolation.

## Validation

`test/NightElf.Runtime.CSharp.Tests` validates:

- allowed dependency loading inside a collectible sandbox
- rejection of non-whitelisted dependencies
- actual sandbox unload via `WeakReference` + forced GC
- timeout behavior through `CancelAfter`
- successful fast-path execution within the timeout window

Repository validation command:

```bash
dotnet test NightElf.slnx
```
