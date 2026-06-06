# Roadmap

Follow-up work for the OctoMesh MCP server. The service is operationally complete as of v1.5 — 176 tools cover the full `octo-cli` command surface, the asset-repo GraphQL transient + persisted query APIs, file I/O, and generic CK CRUD. The three items below are the gaps I'd close next, in priority order.

For the full coverage picture (what's already in vs. what's deferred by design vs. what's never been on the menu), see the changelog in [README.md](README.md) and the architecture sections in [CLAUDE.md](CLAUDE.md).

---

## 1. Persisted-query execution ✅ Shipped in v1.5

`execute_runtime_query` and `execute_stream_data_query` load the persisted query entity by RtId, dispatch on its CK subtype (`RtSimpleRtQuery` / `RtAggregationRtQuery` / `RtGroupingAggregationRtQuery` for runtime; `RtSimpleSdQuery` / `RtAggregationSdQuery` / `RtGroupingAggregationSdQuery` / `RtDownsamplingSdQuery` for stream-data), and execute via the matching engine method. Runtime overrides (`extraFilters` AND-combined with the persisted filter, plus `from`/`to`/`limit`/`sourceRtIds` for stream-data) preserve the studio's runtime-arg semantics. See the changelog and the *Persisted-query execution* section in [CLAUDE.md](CLAUDE.md).

---

## 2. Stream-data filter operator extension

**Status**: Detail gap. Engine supports more operators than MCP currently exposes.

Today the stream-data tools accept `FieldFilterCriteriaDto` and map it to the engine's `FieldFilter`. The mapping covers `Equals` / `NotEquals` / `Greater*` / `Less*` / `In` / `NotIn` / `Between`. Unknown operators silently fall back to `Equals`. The engine also has:

- `Like` — SQL-style wildcard string match
- `MatchRegEx` — regex match
- `AnyEq` / `AnyLike` — element-wise checks for scalar-array fields
- `IsNull` / `IsNotNull` — null-presence predicates

`Like` and the null predicates are the most often-asked-for in practice — operators like "give me all sensors whose Name contains 'inverter'" or "rows whose ErrorReason is set" are common AI prompts that today silently degrade to an equality check.

### Sketch

- Extend `FilterOperatorDto` with the missing values
- Extend the operator switch in `StreamDataAggregationTools.MapFilterOperator`
- Extend the runtime operator switch in `RuntimeAggregationTools.BuildTypedFilters` (same gap exists there)
- Add tests for the new branches (~6 tests)
- Update the AggregationMapper section in CLAUDE.md to drop the "unknown operators fall back to Equals" caveat

### Effort

~½ day. Mechanical extension, no design questions.

---

## 3. Cascade-rollup logical-path back-resolution

**Status**: Known caveat in `get_rollup_query_metadata`.

For a single-step rollup (raw archive → rollup), the tool returns the physical source paths from the rollup's aggregation specs, which match the CK attribute paths on the source. Fine.

For a **cascade rollup** (rollup → rollup), the source paths are the intermediate rollup's physical columns (`_sum`, `_count`, etc.) — not the original CK attribute paths the user would recognize. The GraphQL resolver in asset-repo-services handles this via `RollupLogicalPathResolver` in `Runtime.Engine.CrateDb`, walking back through the chain.

That resolver isn't referenced by the MCP server (it'd add a backend package the MCP doesn't otherwise need). Adding it is straightforward but pulls in transitive deps.

### Sketch

- Add `Meshmakers.Octo.Runtime.Engine.CrateDb` PackageReference to `McpServices.csproj`
- In `get_rollup_query_metadata`, when the rollup's source is itself a rollup, call `RollupLogicalPathResolver.ResolveAsync(snapshot, getArchive, getRollup, ct)` and return its result instead of the raw `Aggregations.SourcePath` list
- Test: stub a 2-step cascade chain via the rollup runtime store mock

### Effort

~½ day. The exception: if adding the CrateDb package breaks anything in the dep graph, it could grow.

### Why it's #3 not #1

Cascade rollups are rare in practice — most installations use single-step rollups. The current behaviour isn't *wrong*, it's just less helpful for the niche case. Worth doing eventually; not blocking.

---

## 4. `available_archive_paths` introspection tool

**Status**: Not implemented. Would mirror `OctoQuery.availableArchivePaths`.

When configuring a new CkArchive, the studio shows a picker of available CK-attribute paths that can be used as columns. The GraphQL resolver walks the CK type graph with a depth cap and returns flat attribute paths annotated with their value types. A future studio-like AI workflow ("set up an archive for this sensor type") would need this.

### Sketch

- New tool: `get_available_archive_paths(ckTypeId, maxDepth = 5)`
- Reuse: the existing `IRtTenantRepository.GetCkTypeGraphAsync` already in the family-2 toolchain
- Walk the type graph, emit `{ path, valueType, isOptional }` records
- Likely lives in `SchemaDiscoveryTools.cs` to stay with family 2

### Effort

~½ day. Specialised introspection; small surface.

### Why it's #4

Useful but situational — the AI only needs it during archive setup, which is a low-frequency operation. The existing `get_type_schema` covers 80% of the same need (it returns the same attribute list, just without the archive-specific recursion cap).

---

## What's deliberately not on this list

- **GraphQL Subscriptions** (`OctoSubscriptions.cs`) — needs an MCP-side notification channel; that's an architecture decision, not a roadmap item.
- **SignalR hubs** (`IAdapterHub`, `IPoolHub`) — service-to-service real-time, no plausible AI use case.
- **Bot / Notifications commands** — gated by the CLI side; they're commented out in `octo-cli/src/ManagementTool/Program.cs`. When that lands there, mirroring it here is mechanical.
- **OIDC flows beyond Device Authorization** — Auth Code / Client Credentials would only matter if the MCP server itself acted as a client to a different IdP; not the design.
- **Per-CK-type sub-fields** (`Runtime.{TypeName}.create` etc.) — covered functionally by the generic CRUD tools (`create_entity` with a `ckTypeId`); per-type wrappers would be code generation, not new capability.
