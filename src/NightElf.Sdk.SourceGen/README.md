# NightElf.Sdk.SourceGen

Incremental Source Generator for NightElf contract dispatch.

Current scope:

- scans `[ContractMethod]` members on `partial` contracts
- generates `DispatchCore` and `DescribeResourcesCore` overrides without runtime reflection
- validates codec-compatible method signatures at compile time
