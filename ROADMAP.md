# Roadmap

Follow-up work for the OctoMesh MCP server. As of v1.5.3 the original four-item roadmap is closed — 177 tools cover the full `octo-cli` command surface, the asset-repo GraphQL transient + persisted query APIs (with the full engine filter-operator set and cascade-rollup back-resolution), the `availableArchivePaths` studio introspection, file I/O, and generic CK CRUD.

This file is now a record of what shipped and what was deliberately left off the list. If new gaps surface in practice, add them above the "deliberately not on this list" section.

For the full coverage picture (what's already in vs. what's deferred by design vs. what's never been on the menu), see the changelog in [README.md](README.md) and the architecture sections in [CLAUDE.md](CLAUDE.md).

---

## 1. Persisted-query execution ✅ Shipped in v1.5

`execute_runtime_query` and `execute_stream_data_query` load the persisted query entity by RtId, dispatch on its CK subtype (`RtSimpleRtQuery` / `RtAggregationRtQuery` / `RtGroupingAggregationRtQuery` for runtime; `RtSimpleSdQuery` / `RtAggregationSdQuery` / `RtGroupingAggregationSdQuery` / `RtDownsamplingSdQuery` for stream-data), and execute via the matching engine method. Runtime overrides (`extraFilters` AND-combined with the persisted filter, plus `from`/`to`/`limit`/`sourceRtIds` for stream-data) preserve the studio's runtime-arg semantics. See the changelog and the *Persisted-query execution* section in [CLAUDE.md](CLAUDE.md).

---

## 2. Stream-data filter operator extension ✅ Shipped in v1.5.1

`FilterOperatorDto` now covers the full engine `FieldFilterOperator` set — `Like`, `AnyEq`, `AnyLike` joined the existing values; `IsNull`, `IsNotNull`, `Regex`, `Contains`, `StartsWith`, `EndsWith` are now wired through the stream-data mapping (they were already present in the runtime CRUD switch but silently dropped on the stream-data side). Both `StreamDataAggregationTools.MapFilterOperator` and `RuntimeAggregationTools.BuildTypedFilters` now throw on an unknown DTO value rather than degrading to `Equals` — the typo-masking behavior is gone. See the *Filter operator coverage* section in [CLAUDE.md](CLAUDE.md).

---

## 3. Cascade-rollup logical-path back-resolution ✅ Shipped in v1.5.2

`get_rollup_query_metadata` now back-resolves cascade rollups via `RollupLogicalPathResolver` (from the `Meshmakers.Octo.Runtime.Engine.CrateDb` package, added as a direct dependency). For a single-step rollup (raw → rollup) the spec's `SourcePath` is returned as-is; for cascade rollups (rollup → rollup) the physical `_sum`/`_count` storage columns are walked back through the parent's aggregation specs until a raw / time-range archive is hit, so the studio picker shows the original CK attribute paths the operator would recognize. Specs whose chain is broken (missing parent, store inconsistency) are silently dropped per the resolver contract.

---

## 4. `get_available_archive_paths` introspection tool ✅ Shipped in v1.5.3

`get_available_archive_paths(ckTypeId, maxDepth = 5)` walks the CK type/record graph from the given CK type and emits one row per reachable attribute path — primitive type, `IsRecord`, `IsArray`, `RecordTypeId`. Bounded by `maxDepth` (default 5, clamped to ≥1) so recursive records terminate predictably; a visited-record set prevents infinite recursion on self-referential records (tree-shaped records whose child slot points back at the parent type). Mirrors the asset-repo `Octo.availableArchivePaths` GraphQL resolver — the AI now has the same studio-style picker the operator uses when composing a new CkArchive.

---

## What's deliberately not on this list

- **GraphQL Subscriptions** (`OctoSubscriptions.cs`) — needs an MCP-side notification channel; that's an architecture decision, not a roadmap item.
- **SignalR hubs** (`IAdapterHub`, `IPoolHub`) — service-to-service real-time, no plausible AI use case.
- **Bot / Notifications commands** — gated by the CLI side; they're commented out in `octo-cli/src/ManagementTool/Program.cs`. When that lands there, mirroring it here is mechanical.
- **OIDC flows beyond Device Authorization** — Auth Code / Client Credentials would only matter if the MCP server itself acted as a client to a different IdP; not the design.
- **Per-CK-type sub-fields** (`Runtime.{TypeName}.create` etc.) — covered functionally by the generic CRUD tools (`create_entity` with a `ckTypeId`); per-type wrappers would be code generation, not new capability.
