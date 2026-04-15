# Phase 3 Resource Declaration Source Generator

Issue: `#10`

## Goal

Replace reflection-based contract resource extraction with compile-time generated descriptors so the parallel execution path can prepare read/write sets without touching runtime reflection.

## What Landed

### SDK runtime surface

`NightElf.Sdk.CSharp` now exposes:

- `ContractResourceSet` as the runtime read/write key container
- `ContractMethodAttribute.ReadExtractor` and `.WriteExtractor` for compile-time resource declarations
- `CSharpSmartContract.DescribeResources(...)` and `SupportsResourceExtraction` for generated contracts

Contracts without generated resource descriptors keep the base behavior:

- `SupportsResourceExtraction == false`
- `DescribeResourcesCore(...)` falls back to `ContractResourceSet.Empty`

### Source generator output

`ContractDispatcherGenerator` now emits both:

- `DispatchCore(...)`
- `DescribeResourcesCore(...)`

For methods that declare resource extractors, the generated code:

1. decodes the contract input once when an extractor needs it
2. calls the declared read/write extractor methods directly
3. returns a normalized `ContractResourceSet`

Unknown methods still throw `ContractMethodNotFoundException`; invalid payloads still throw `ContractInputDecodeException`.

### Parallel execution entrypoint

`NightElf.Kernel.Parallel` is now a real project with `ResourceExtractionService`.

Behavior:

- generated contracts: use compiled `DescribeResources(...)` directly
- legacy/no-generator contracts: return empty resources and mark `UsedFallback = true`

This removes the need for runtime reflection in the resource extraction path and gives the next MVCC/conflict-detection steps a stable read/write set shape to consume.

## Generator Rules

Resource extractor methods:

- are referenced from `[ContractMethod(ReadExtractor = ..., WriteExtractor = ...)]`
- must live on the same contract type
- may accept zero parameters, or one parameter matching the contract method input type
- must return `IEnumerable<string>` or an equivalent string sequence such as `string[]`

Violations produce compile-time diagnostics:

- `NE1006` missing extractor
- `NE1007` invalid extractor signature

## Validation

`test/NightElf.Sdk.SourceGen.Tests` validates:

- dispatch generation still works
- generated resource extraction returns the declared read/write keys
- unknown methods fail without reflection fallback
- input decode failures surface cleanly
- legacy contracts fall back to empty resources with `UsedFallback = true`
- invalid extractor signatures fail at compile time

Repository validation command:

```bash
dotnet test NightElf.slnx
```
