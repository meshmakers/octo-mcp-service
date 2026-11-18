---
description: Lowercase function enum, AggregationMapper, engine column keys, archive resolution, filter operators and persisted query execution.
applies_to: **/Tools/{RuntimeAggregation,StreamData*}Tools.cs, **/Services/{AggregationMapper,AvailableArchivePaths*}.cs, **/Models/{Aggregation,Filters}/**
---

# Aggregation and stream-data query tools

## Aggregation Tools Architecture

The aggregation + stream-data tools (`RuntimeAggregationTools`, `StreamDataAggregationTools`, `StreamDataMetadataTools`) talk **directly to the runtime engine** — same architectural layer as the generic CRUD tools, but with their own conventions.

### Lowercase function strings — `AggregationFunctionDto`

Counter to the rest of the codebase which uses PascalCase enum names, the aggregation enum uses **lowercase short names** (`count`/`sum`/`avg`/`min`/`max`/`integral`). This is intentional and AI-driven: LLMs construct lowercase strings more reliably than enum-style strings, and lowercase mirrors SQL conventions. The translation to the engine's `AggregationFunction` (which uses `Count/Sum/Average/Minimum/Maximum/Integral`) happens in `AggregationMapper.ToEngineFunction`. Do not "fix" the enum to PascalCase.

`integral` is the step integral of the engine: value unit x hours over `[from, to)`, LOCF, carry-in from the
last row before `from`, rtIds summed, empty window = null, raw archives only. It is accepted **only** by
`query_stream_data_aggregation`, `query_stream_data_grouping` and persisted stream-data aggregation/grouping queries
(`Validate(..., allowIntegral: true)`), and needs `from` and `to` (`ValidateIntegralWindow`). Runtime aggregations, downsampling (transient and
persisted) refuse it up front; a persisted runtime aggregation query carrying the CK value `Integral` fails in `ApplyToAggregationInput`.

### `AggregationMapper` is the single point of validation + engine mapping

Every aggregation tool routes through `Services/AggregationMapper.cs`:

- `Validate(aggregations, allowIntegral)` — at-least-one rule, non-count requires `attributePath`, alias uniqueness, `integral` only where allowed
- `ValidateIntegralWindow(aggregations, from, to)` — `integral` needs `from < to`
- `ValidateGroupBy(paths)` — non-empty list, no blanks, no duplicates
- `DeriveAlias(column)` — `<function>_<sanitised-path>` when no explicit alias (e.g. `avg_Power`); special-case `"count"` for unparametrised count
- `ApplyToAggregationInput(input, columns)` — pushes columns into the runtime engine's `AggregationInput` (used by runtime aggregation tools)
- `ToEngineColumns(columns)` — maps to `AggregationColumn[]` (used by stream-data tools)

When you add a new aggregation tool, **don't bypass these helpers**. The validation outputs are the user-visible error messages — keeping them consistent matters.

### Engine column key convention (stream-data only)

Stream-data aggregation results come back as `StreamDataRow` instances with `Values` keyed `{column}_{token}` by the CrateDB repository
(`power_avg`, `power_integral`; `token` = `AggregationMapper.EngineResultToken`), where `column` is the storage name of the archive's
`StreamDataFieldResolver` (lower-cased concatenated path for a data-stream attribute, the versioned physical name for a computed column,
camelCase for built-ins such as `rtId`). `EngineValue` inside `StreamDataAggregationTools` rebuilds that key (resolver from the archive
snapshot, fallback `EngineColumnName` = the engine's `ColumnNameMapper` rule) and writes the value under the alias from
`AggregationMapper.DeriveAlias`; group-key columns are read the same way. Earlier the tools looked up only `{Function}({path})`
(the `ToString()` of `AggregationColumn`), a key the engine never writes, so every stream-data aggregation value came back null; a later change
removed that fallback, test mocks use the real keys (`obiscode`, `amountvalue_count`). Stream-data `count` needs an `attributePath`
(`AggregationMapper.ValidateStreamDataColumns`): the archive counts non-null values of a column and the engine refuses a blank path as the
unknown column `'*'`; runtime aggregations keep `count` without a path.

### `StreamDataContext` resolves the four-stage cascade

Stream-data tools take an `archiveRtId` (not a `ckTypeId`) — the target CK type is on the archive snapshot. The resolution involves four nullable accessors:

```
ITenantResolutionService.GetTenantContextAsync(tenantId)
    → ITenantContext.GetStreamDataRepository()       → null if StreamData not enabled
    → ITenantContext.GetArchiveRuntimeStore()
        → archiveStore.GetAsync(rtId)                → null if archive not found
        → snapshot.TargetCkTypeId                    → the ckTypeId for the engine call
```

`StreamDataContext.TryResolveAsync` collapses this into a single result with a structured error message per failure mode. Every stream-data tool starts with that call.

### `ITenantResolutionService.GetTenantContextAsync`

Added specifically for the aggregation work — the platform-admin tools only need `ITenantRepository`, but the stream-data accessors live on `ITenantContext` (a wider interface). The implementation calls `ISystemContext.FindTenantContextAsync(tenantId)`. When a future tool needs `GetRollupArchiveRuntimeStore()` or any other context-only accessor, use this same entry point.

### Studio archive-path introspection (`get_available_archive_paths`)

Mirrors the asset-repo GraphQL `Octo.availableArchivePaths` resolver. Walks the CK type/record graph from a starting `ckTypeId` and emits one `ArchivePathInfo` per reachable attribute path: `Path`, `PrimitiveType` (the `AttributeValueTypesDto` name as a string), `IsRecord`, `IsArray`, `RecordTypeId`.

- **Termination**: bounded by `maxDepth` (default 5, clamped to ≥1) so recursive records terminate predictably. Plus a visited-record set in the recursion frame so self-referential records (tree-shaped records whose child slot points back at the parent type) don't infinite-loop — the parent record row is emitted once and re-entry into the same record id is skipped (popping the visited set on the way out so a sibling that references the same record at a different path is still walked).
- **Array-flag propagation**: when the walker descends into a `RecordArray` (or any other array-shaped attribute), the `IsArray` flag carries down into the record's children. A leaf like `Contacts.Email` is therefore `IsArray=true` — the caller can tell apart "this path is a column" from "this path is an element of an array column" without re-reading the parent row.
- **Missing-record fallback**: when `ValueCkRecordId` references a record that isn't in the cache (model partially loaded, cache stale), the record row itself is still emitted but children are skipped — matches the GraphQL resolver and keeps the picker partially useful.
- **No SDK call**: the resolver runs entirely against `ICkCacheService` (the same cache the schema-discovery tools use), so no engine round-trip is needed. The tool calls `LoadCacheForTenantAsync` first to make sure the tenant's CK model is hydrated.

The resolver lives in `Services/AvailableArchivePathsResolver.cs` as an `internal static`. If a future tool needs a different traversal (e.g. include navigation properties, emit only leaf paths), extend the helper rather than duplicating the walk.

### Cascade-rollup back-resolution (`get_rollup_query_metadata`)

The tool returns the *logical* CK-attribute paths a rollup aggregates over, not the physical storage columns. For a single-step rollup (raw → rollup) the spec's `SourcePath` is already a CK attribute path — the resolver returns it verbatim. For cascade rollups (rollup → rollup), the spec's `SourcePath` is a physical column on the parent rollup's table (e.g. `amountValue_sum`); `RollupLogicalPathResolver.ResolveAsync` walks up through the parent's aggregation specs (via `RollupAggregationColumns.Resolve`) until it hits a raw / time-range archive where the path is finally logical. The MCP server passes two callbacks: `getArchive` (from `ITenantContext.GetArchiveRuntimeStore()`) and `getRollup` (from `GetRollupArchiveRuntimeStore()`). Broken chains (missing parent, store inconsistency) are silently dropped per the resolver contract — a single broken spec must not blank the entire picker.

The resolver lives in the `Meshmakers.Octo.Runtime.Engine.CrateDb` package, which is a direct `McpServices.csproj` dependency. It pulls in Npgsql + Dapper + Polly.Core transitively, but only the `RollupLogicalPathResolver` + `RollupAggregationColumns` static helpers are used — no DB connection is established by the MCP server itself.

### Pre-SDK validation matters

These tools return `IsSuccess=false` + a clear `ErrorMessage` for:
- empty aggregation list
- non-count function without attributePath
- duplicate aliases
- empty / duplicate group-by paths
- invalid time windows (`from >= to`, `limit <= 0`)

Without this, the engine throws on the SDK side, which surfaces as a 500-style exception with less context. The AI client reads `ErrorMessage` and can fix its tool call directly.

### Filter operator coverage

`FilterOperatorDto` mirrors the engine's `FieldFilterOperator`. The DTO set is: `Equals` / `NotEquals` / `Contains` / `StartsWith` / `EndsWith` / `GreaterThan` / `GreaterThanOrEqual` / `LessThan` / `LessThanOrEqual` / `Between` / `In` / `NotIn` / `IsNull` / `IsNotNull` / `Regex` / `Like` / `AnyEq` / `AnyLike`.

- **Substring vs SQL pattern**: `Contains` / `StartsWith` / `EndsWith` take a plain substring; `Like` takes a `%`-wildcard pattern. Prefer the dedicated ops when you don't need wildcards — they're cheaper and clearer.
- **Array predicates**: `AnyEq` and `AnyLike` only apply to scalar-array CK attributes; they test "any element matches". Using them on a non-array attribute is an engine-side error, not pre-validated.
- **Null predicates**: `IsNull` and `IsNotNull` ignore the `value` field on `FieldFilterDto`.
- **No silent fallback**: every operator maps explicitly. `StreamDataAggregationTools.MapFilterOperator` and `RuntimeAggregationTools.BuildTypedFilters` throw `ArgumentOutOfRangeException` on an unknown DTO value rather than silently mapping to `Equals` (the pre-v1.5.1 behavior, which masked filter typos). The CRUD-side `RuntimeEntityCrudTools.ApplyFieldFilter` already threw.

When adding a new engine operator, extend the DTO + both switches + add a `[Theory]` row in `FilterOperatorMappingTests`.

### Persisted-query execution (`execute_runtime_query` + `execute_stream_data_query`)

These two tools execute a *stored* query entity by RtId. The pattern is: load the entity, dispatch on its CK subtype, build the engine-side query options from the persisted state, optionally merge in runtime overrides, execute, project the result.

- **Loading**: `tenantRepository.GetActiveRtEntityByRtIdAsync<RtPersistentQuery>` / `<RtStreamDataQuery>` (engine extension) — returns `null` for a missing or deleted (archived) query; the generic overload uses the entity's CK type from its base, so callers don't have to thread a `ckTypeId` separately.
- **Dispatch on CK subtype** (using `switch` on runtime type, mirroring the GraphQL resolver):
  - Runtime side: `RtSimpleRtQuery` → entity DTOs filtered to the persisted `Columns` list (reuses `RuntimeEntityCrudTools.FilterAttributes` for nested record/sub-path support); `RtAggregationRtQuery` → scalar projection via `AggregationInput.AggregateResult`; `RtGroupingAggregationRtQuery` → grouped projection via `AggregateFieldGroupBy`.
  - Stream side: `RtSimpleSdQuery` / `RtAggregationSdQuery` / `RtGroupingAggregationSdQuery` / `RtDownsamplingSdQuery` map to the four `IStreamDataRepository.Execute*Async` methods. The persisted `ArchiveRtId` is read off the entity — no separate argument.
- **CK enum → MCP enum**: `AggregationMapper.MapCkAggregationName` translates the CK `AggregationTypes` enum string names (`Count`/`Sum`/`Average`/`Minimum`/`Maximum`, plus the short forms `Avg`/`Min`/`Max`) to `AggregationFunctionDto`. The persisted aggregation columns then go through the same `ApplyToAggregationInput` / `ToEngineColumns` helpers as the transient tools — the projection layer doesn't need to know whether the columns came from a runtime arg or a persisted entity.
- **Runtime overrides**: `extraFilters` is AND-combined with the persisted `FieldFilter` for both tools. Stream-data adds `fromOverride` / `toOverride` / `limitOverride` / `sourceRtIdsOverride` — each falls back to the persisted value when omitted. The merge semantics (extra AND persisted) mirror `StreamDataQueryDtoType.MergeFilters` in asset-repo-services so the studio's runtime-arg behavior is preserved across both APIs.
- **Pre-SDK validation**: empty queryRtId, entity not found (a deleted query included), missing ArchiveRtId on stream queries, empty `GroupingColumns` on grouped subtypes, and downsampling-specific `from < to` + positive `limit` requirements all surface as `IsSuccess=false` with an actionable message.

The response envelope `PersistedRuntimeQueryResponse` / `PersistedStreamDataQueryResponse` discriminates by `QuerySubtype` so the AI client knows whether `Entities` (simple) or `Rows` (aggregation) carries the payload.
