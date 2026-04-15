# AsyncHelper.RunSync Audit

## Scope

This note captures the NightElf Phase 2 audit for `AsyncHelper.RunSync()` usage after the state-read fast path from issue `#6` landed.

Snapshot date: 2026-04-15

Audit command:

```bash
rg -n 'AsyncHelper\.RunSync\(' src test
```

## Result

- `src/`: `0` call sites
- `test/`: `0` call sites

The remaining references are documentation-only:

- `docs/001-refactoring-overview.md`
- `README.md`
- `README_zh.md`

Those references describe the legacy AElf design and the NightElf migration goal. They are not executable code paths.

## State Read Hot Path

NightElf's current hot path no longer depends on `AsyncHelper.RunSync()`:

1. `TieredStateCache.TryGet(...)` handles L1 in-memory hits synchronously.
2. `NotModifiedCachedStateStore.TryGetCached(...)` handles L2 cached hits synchronously.
3. `CachedStateReader.TryGetState(...)` exposes the explicit synchronous entry point for callers.
4. `CachedStateReader.GetStateAsync(...)` falls back to the backing store only on cache miss.

This keeps cache-hit reads on a synchronous fast path while preserving async I/O only for cold misses.

## Remaining Work Classification

- Current production code: no retained `AsyncHelper.RunSync()` usage.
- Future smart-contract bridge/runtime work: keep `RunSync` banned; prefer `TryGetState(...)` for hot reads and true async for cold paths.
- Documentation: keep historical references until the corresponding runtime and bridge modules are implemented, so the migration rationale stays explicit.
