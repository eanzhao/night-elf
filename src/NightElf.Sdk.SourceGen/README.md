# NightElf.Sdk.SourceGen

Incremental Source Generator for NightElf contract dispatch.

Current scope:

- scans `[ContractMethod]` members on `partial` contracts
- generates `DispatchCore` overrides without runtime reflection
- validates codec-compatible method signatures at compile time
