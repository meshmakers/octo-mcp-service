# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

`octo-mcp-service` is the **Model Context Protocol** server for OctoMesh. It exposes ~199 tools that mirror the full `octo-cli` command surface plus generic CK-type CRUD, so AI assistants can administer the platform end-to-end without invoking the CLI.

Three distinct tool families live here — be aware which one you're touching:

1. **Platform-admin tools** — thin wrappers over the `Meshmakers.Octo.Sdk.ServiceClient` SDK. One tool per `octo-cli` command. These tools talk HTTP to the Identity / Asset / Communication / Reporting / StreamData / Bot services.
2. **Generic CK CRUD + schema tools** — predate the platform-admin tools and talk directly to the runtime engine (MongoDB) via `ITenantRepository`. These do not use the SDK service clients.
3. **Aggregation + stream-data query tools** — newer; mirror the asset-repo GraphQL transient-query surface. They share family 2's path (talk to the engine directly via `ITenantRepository` / `ITenantContext.GetStreamDataRepository`), but use the lowercase `AggregationFunctionDto` enum and the `AggregationMapper` helper — *not* the platform-admin `*ClientContext` pattern.

If you're adding a tool that mirrors an `octo-cli` command, you're in family 1 — follow the `*ClientContext` pattern below. If you're adding a runtime/stream-data read or aggregation, you're in family 2 or 3.

## Build & Test Commands

```bash
# Build the MCP server
dotnet build src/McpServices/McpServices.csproj -c DebugL

# Build the entire solution (server + tests + resources)
dotnet build Octo.McpServices.sln -c DebugL

# Run all tests (currently 839, ~1 s)
dotnet test Octo.McpServices.sln -c DebugL

# Filter tests by class
dotnet test --filter "FullyQualifiedName~TenantManagementToolsTests"

# Run dev server (binds to 5017 by default — see launchSettings.json)
cd src/McpServices && dotnet run --environment Development
```

**Build configurations:** `Debug`, `Release`, `DebugL` (local dev with `OctoVersion=999.0.0`, uses local NuGet packages from `../nuget/`).

**`TreatWarningsAsErrors` is enabled.** In particular, `CS1591` (missing XML doc) breaks the build for any public member of `McpServices`. Every public type, property, and method on a new tool class needs an XML doc summary.

## Mandatory Conventions (read before adding code)

### 1. Every new tool MUST have unit tests

Minimum coverage per tool:

- **Happy path** — mock the SDK client, return realistic DTO, assert the tool returned `IsSuccess = true` and called the right SDK method with the right arguments.
- **Unauthenticated** — `GivenUnauthenticated()`, assert `IsSuccess = false` and `ErrorMessage` contains `"Not authenticated"`. No SDK call.
- **Missing required args** — pass empty / null, assert validation error, no SDK call.
- **Destructive without confirm** — for any tool with a `confirm` parameter, assert refusing without it.

The current ratio is ~4.2 tests per tool (839 tests for 199 tools). Don't lower it.

### 2. Use the `*ClientContext` helpers — never call the factory directly from a tool

Every SDK-backed tool starts the same way:

```csharp
var ctx = IdentityClientContext.TryBuild(server, tenantId);
if (ctx.Error != null)
{
    return new MyResponse { IsSuccess = false, ErrorMessage = ctx.Error };
}

// ctx.Client is the IIdentityServicesClient, ctx.TenantId is the resolved tenant
```

Six context helpers exist in `src/McpServices/Services/`:

| Context | Backing SDK Client | Tenant routing |
|---|---|---|
| `IdentityClientContext` | `IIdentityServicesClient` | per-tenant (`{tenantId}/v1`) |
| `AssetClientContext` | `IAssetServicesClient` | per-tenant |
| `CommunicationClientContext` | `ICommunicationServicesClient` | per-tenant (`{tenantId}/v1`) — AB#4287, no system fallback |
| `StreamDataClientContext` | `IStreamDataServicesClient` | per-tenant (`{tenantId}/v1`) — AB#4287, was `api/v1` |
| `ReportingClientContext` | `IReportingServicesClient` | per-tenant (`{tenantId}/v1`) — AB#4287, no system fallback |
| `BotClientContext` | `IBotServicesClient` | system-scoped |

For `Bot` system-scoped one-offs (e.g., `reconfigure_log_level` dispatch), grab it via `server.Services.GetRequiredService<IOctoServiceClientFactory>()` directly — there is no helper because the call sites are too few.

### 3. Tool method signature pattern

```csharp
[McpServerTool(Name = "my_snake_case_tool")]
[Description("Equivalent to octo-cli MyCommand. Plus a sentence about what it does.")]
public static async Task<MyResponse> MyTool(
    McpServer server,
    [Description("Required arg description.")] string requiredArg,
    [Description("Optional arg description.")] bool? optionalArg = null,
    [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
```

- Method is `static async Task<TResponse>`.
- First param is `McpServer server` — never `IMcpServer`.
- Every parameter gets a `[Description]` attribute. The descriptions become the AI's documentation; write them as if explaining to a colleague.
- `tenantId` is the last optional parameter on every tenant-scoped tool.
- Tool name is `snake_case` and mirrors the CLI command verb (e.g. CLI `CreateTenant` → MCP `create_tenant`).

### 4. Response envelope

Every tool returns a structured response with these fields at minimum:

```csharp
public class MyResponse
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Message { get; set; }
    public string? TenantId { get; set; }
    // ... tool-specific payload
}
```

- **Never throw** out of a tool. Catch exceptions and put `ex.Message` into `ErrorMessage`. The MCP framework will serialise whatever you return.
- **Never write to `Console.WriteLine`** or `ILogger.LogInformation` for user-visible output. The MCP transport doesn't surface stdout to the AI client.
- `IsSuccess = false` + `ErrorMessage` is how you communicate problems. The AI client reads these and reasons about next steps.

### 5. Destructive operations require `confirm: true`

The CLI uses an interactive `(y/N)` prompt via `IConfirmationService`. MCP can't do that. Instead:

```csharp
public static async Task<MyResponse> DeleteThing(
    McpServer server,
    string thingId,
    [Description("Must be true to actually delete.")] bool confirm = false,
    string? tenantId = null)
{
    if (!confirm)
    {
        return new MyResponse
        {
            IsSuccess = false,
            ErrorMessage = $"Refusing to delete '{thingId}' without confirm=true."
        };
    }
    // ... actually do it
}
```

Test the refusal path. Never default `confirm = true`. Never silently skip the check for "convenience" inside a batch helper — every destructive call goes through the confirm gate.

### 6. SDK DTOs go on the wire as-is

The MCP framework serialises whatever you return. Returning SDK DTOs (`UserDto`, `ClientDto`, `BlueprintApplyResultDto`, etc.) directly is the convention — no MCP-side translation layer. If the SDK changes a DTO shape, the MCP response changes with it, and that's intentional.

For composite responses (list + count + tenant id), define your own wrapper DTO in `src/McpServices/Models/*Responses.cs`. Group by domain (`IdentityResponses.cs`, `AssetResponses.cs`, etc.) — don't make one file per response type.

### 7. Per-request SDK clients (never singleton)

The SDK clients cache their `ServiceUri` on first use. Sharing one client across multiple tenants → wrong tenant in the URL on the second call. **Always** go through `IOctoServiceClientFactory.Create*Client(tenantId, accessToken)` — it returns a fresh instance.

The `*ClientContext.TryBuild` helpers handle this for you. Don't manually construct SDK clients in tool code.

### 8. Risk classification (`[McpRisk]`) for AI Adapter approval gating

Tools have an optional `[McpRisk(McpRiskLevel.Low|Medium|High)]` attribute that classifies their blast radius. The AI Adapter worker calls `get_tool_risk_metadata` once at session start and uses the result to decide whether a tool call needs to be routed through its user-facing approval gate before running.

**This is not authorisation.** Authorisation is delegated to the backend services via the propagated OAuth token. `McpRisk` is informational metadata that the worker reads to drive its own safety story.

Classification convention:

- **Low** (default — omit the attribute): read-only operations, schema introspection, single-instance create/update with narrow scope.
- **Medium**: single-instance deletes, schema-introspection-driven actions, anything where audit matters more than blocking. Worker logs but does not pause.
- **High**: destructive or schema-changing operations — bulk delete, dropping a CK type / attribute / enum value, production deploy, force-push, blueprint install/uninstall/apply-update against a tenant. Worker pauses on PreToolUse and surfaces the proposed call to the user for approval.

Place the attribute next to `[McpServerTool]`:

```csharp
[McpServerTool(Name = "delete_entity")]
[McpRisk(McpRiskLevel.Medium)]
[Description("Delete an entity by its runtime ID")]
public static async Task<DeleteEntityResponse> DeleteEntity(...)
```

`ToolRiskRegistry` reflects over the McpServices assembly at startup; tools without the attribute resolve as `Low`. When you add a new tool, decide the level at the same time as the implementation — flipping later is a behaviour-change for any consumer that already cached the registry.

### 9. Optimistic locking on `update_entity` / `delete_entity`

Concurrent AI sessions can write to the same runtime entity. `update_entity` and `delete_entity` accept an optional `expected_version` (the `RtVersion` the caller observed on its prior read):

- **Omitted** → last-write-wins, identical to pre-#4111 behaviour. `update_entity` still bumps `RtVersion` on the way out so a later optimistic call sees a meaningful token.
- **Matches stored** → the write/delete proceeds, the response carries the bumped `CurrentRtVersion`.
- **Stale** → no write/delete happens. Response is `IsSuccess=false`, `IsConflict=true`, and carries `CurrentRtVersion` + the current `Entity` payload — enough for the caller to rebase its change without a second `get_entity_by_id` round-trip.

The tool layer increments `RtVersion` explicitly because the engine's `UpdateOneRtEntityByIdAsync` path does not (auto-bump lives only in `BulkRtMutation`). The increment saturates at `ulong.MaxValue` to avoid `OverflowException` on the pathological case.

Caller pattern:

```
read = get_entity_by_id(...)            // read.entity.rtVersion = 7
edit = mutate(read.entity)
res  = update_entity(..., expected_version: 7)
if (res.is_conflict) {
    // res.entity is the current row; rebase or surface to user
    edit2 = merge(res.entity, ...)
    update_entity(..., expected_version: res.current_rt_version)
}
```

When you add a new write tool (single-entity create / update / delete pattern), wire `expected_version` the same way and bump `RtVersion` on commit. Don't reach for `RtChangedDateTime` as an alternative token — it survives blueprint writes that `RtVersion` doesn't, but timestamp ties at sub-millisecond resolution are real and the token must be monotonic-per-write.

## File I/O Architecture

Tools that need to receive or produce files use a separate HTTP channel: the JSON-RPC tool call coordinates an opaque transfer id, and the actual bytes flow through `FileTransferController` at `/file-transfer/{upload,download}/{id}`.

### Components

- `IFileTransferStore` / `FileTransferStore` — in-memory + disk-backed buffers. Reservations live in `_pending`; completed uploads in `_uploads`; pending downloads in `_downloads`. Files land in `Path.GetTempPath()/octo-mcp-file-transfer/<random>/`.
- `FileTransferSweeper` — `BackgroundService` that purges expired entries + their files every 5 min.
- `FileTransferController` — `PUT /file-transfer/upload/{id}` writes the body to the reserved path (5 GiB cap, streaming chunked). `GET /file-transfer/download/{id}` streams the file with range support.
- `JobPollingHelper` — generic async-job poller for asset + bot service jobs (Succeeded/Failed/Timeout).

### Upload-then-import flow

```
prepare_file_upload(fileName) → { transferId, uploadUrlPath }
HTTP PUT to <publicUrl>/file-transfer/upload/{transferId}
import_ck_model(transferId, tenantId) → waits for job, returns jobId
```

Inside the import tool: `store.GetUpload(transferId)` returns the on-disk path; pass that to the SDK call (which requires a file path argument, e.g. `ImportCkModelAsync(tenantId, filePath)`). On success, `store.DeleteUpload(transferId)` to clean up.

### Export-then-download flow

```
export_runtime_model_by_query(queryId) → starts asset job → polls → bot downloads to temp file → store.RegisterDownload(...) → returns { transferId, downloadUrlPath }
HTTP GET <publicUrl>/file-transfer/download/{transferId}
```

### Security

Transfer ids are random 128-bit GUIDs in URL paths; they expire in 30 min; no extra auth check on the endpoints. For stricter setups, put the service behind your own auth gateway. **Do not** add base64-in-tool-parameter as an alternative path — the file-transfer endpoints are the only sanctioned mechanism for binary payloads.

### CK + runtime model upload formats (gotchas)

`import_ck_model` and `import_runtime_model` are NOT JSON-only — confirmed accepted formats:

- **Single compiled YAML** from the CK MSBuild output at `bin/<config>/net10.0/octo-ck-libraries/<Project>/out/ck-<name>-<major>.yaml`. Easiest path after a `dotnet build`.
- **Single compiled JSON** in the same shape as files under `~/.octo/local-catalog/ck-models/v2/<letter>/<Model>/<major>/ck-<name>-<version>.json`.
- **Zip containing the source ConstructionKit/ folder** (ckModel.yaml + types/ + enums/ + attributes/ + associations/ + records/).
- For runtime models: a single YAML/JSON conforming to `runtime-model.schema.json` (an `entities:` list keyed by `rtId` + `ckTypeId`), or a zip thereof.

The tool description says "PUT the file to the returned URL" — historically said "PUT the JSON/zip" which was misleading. Asset-services accepts all of the above; schema validation happens server-side after the file lands in the file-transfer store.

### Service-managed CK models — don't use import_ck_from_catalog

The CK library status flags every model as either user-managed or **service-managed** (`isServiceManaged: true`). Service-managed models include `System` (always), `System.Communication`, `System.StreamData`, `System.Reporting`, `System.UI`, `System.Ai`, `System.Bot`, `System.Identity`, `System.Notification` — anything that backs a backend service feature.

For service-managed models, `import_ck_from_catalog` will silently no-op even when the model is NOT loaded in the target tenant. The tool returns `IsSuccess=true` with messages like "Enqueued 0 import job(s)" or "Nothing to import — already up to date", but `get_available_models` will not list the model afterwards. Misleading but consistent.

The correct way to make those models available is the matching `enable_<feature>` tool:

| Service-managed model | Enable tool |
|---|---|
| `System.Communication-*` | `enable_communication` |
| `System.StreamData-*` | `enable_stream_data` |
| `System.Reporting-*` | `enable_reporting` |
| `System.UI-*` | (no MCP tool yet — install via Studio or octo-cli) |

For user-managed CK models (Basic.*, Industry.*, EnergyIQ, Loxone, custom tenant models), `import_ck_from_catalog` works correctly and DOES load them, even though the same "Enqueued 0 import job(s)" message appears. The reliable verification is `get_ck_library_status` — it reports the actually-loaded version and `modelState=Available`. `get_available_models` may be stale right after an import.

## Aggregation Tools Architecture

The aggregation + stream-data tools (`RuntimeAggregationTools`, `StreamDataAggregationTools`, `StreamDataMetadataTools`) talk **directly to the runtime engine** — same architectural layer as the generic CRUD tools, but with their own conventions.

### Lowercase function strings — `AggregationFunctionDto`

Counter to the rest of the codebase which uses PascalCase enum names, the aggregation enum uses **lowercase short names** (`count`/`sum`/`avg`/`min`/`max`). This is intentional and AI-driven: LLMs construct lowercase strings more reliably than enum-style strings, and lowercase mirrors SQL conventions. The translation to the engine's `AggregationFunction` (which uses `Count/Sum/Average/Minimum/Maximum`) happens in `AggregationMapper.ToEngineFunction`. Do not "fix" the enum to PascalCase.

### `AggregationMapper` is the single point of validation + engine mapping

Every aggregation tool routes through `Services/AggregationMapper.cs`:

- `Validate(aggregations)` — at-least-one rule, non-count requires `attributePath`, alias uniqueness
- `ValidateGroupBy(paths)` — non-empty list, no blanks, no duplicates
- `DeriveAlias(column)` — `<function>_<sanitised-path>` when no explicit alias (e.g. `avg_Power`); special-case `"count"` for unparametrised count
- `ApplyToAggregationInput(input, columns)` — pushes columns into the runtime engine's `AggregationInput` (used by runtime aggregation tools)
- `ToEngineColumns(columns)` — maps to `AggregationColumn[]` (used by stream-data tools)

When you add a new aggregation tool, **don't bypass these helpers**. The validation outputs are the user-visible error messages — keeping them consistent matters.

### Engine column key convention (stream-data only)

Stream-data aggregation results come back as `StreamDataRow` instances with `Values` keyed by the engine's column name format `{Function}({path})` — the `ToString()` of `AggregationColumn`. The projection layer rebuilds the same key (`EngineColumnKey` helper inside `StreamDataAggregationTools`) to look up each value, then writes it under the MCP-side alias from `AggregationMapper.DeriveAlias`. Group-key columns flow straight from `Values` into the response dict, indexed by the group-by attribute paths the caller supplied.

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

- **Loading**: `ITenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>` / `<RtStreamDataQuery>` — the generic GetRtEntity overload uses the entity's CK type from its base, so callers don't have to thread a `ckTypeId` separately.
- **Dispatch on CK subtype** (using `switch` on runtime type, mirroring the GraphQL resolver):
  - Runtime side: `RtSimpleRtQuery` → entity DTOs filtered to the persisted `Columns` list (reuses `RuntimeEntityCrudTools.FilterAttributes` for nested record/sub-path support); `RtAggregationRtQuery` → scalar projection via `AggregationInput.AggregateResult`; `RtGroupingAggregationRtQuery` → grouped projection via `AggregateFieldGroupBy`.
  - Stream side: `RtSimpleSdQuery` / `RtAggregationSdQuery` / `RtGroupingAggregationSdQuery` / `RtDownsamplingSdQuery` map to the four `IStreamDataRepository.Execute*Async` methods. The persisted `ArchiveRtId` is read off the entity — no separate argument.
- **CK enum → MCP enum**: `AggregationMapper.MapCkAggregationName` translates the CK `AggregationTypes` enum string names (`Count`/`Sum`/`Average`/`Minimum`/`Maximum`, plus the short forms `Avg`/`Min`/`Max`) to `AggregationFunctionDto`. The persisted aggregation columns then go through the same `ApplyToAggregationInput` / `ToEngineColumns` helpers as the transient tools — the projection layer doesn't need to know whether the columns came from a runtime arg or a persisted entity.
- **Runtime overrides**: `extraFilters` is AND-combined with the persisted `FieldFilter` for both tools. Stream-data adds `fromOverride` / `toOverride` / `limitOverride` / `sourceRtIdsOverride` — each falls back to the persisted value when omitted. The merge semantics (extra AND persisted) mirror `StreamDataQueryDtoType.MergeFilters` in asset-repo-services so the studio's runtime-arg behavior is preserved across both APIs.
- **Pre-SDK validation**: empty queryRtId, entity not found, missing ArchiveRtId on stream queries, empty `GroupingColumns` on grouped subtypes, and downsampling-specific `from < to` + positive `limit` requirements all surface as `IsSuccess=false` with an actionable message.

The response envelope `PersistedRuntimeQueryResponse` / `PersistedStreamDataQueryResponse` discriminates by `QuerySubtype` so the AI client knows whether `Entities` (simple) or `Rows` (aggregation) carries the payload.

## Authentication & Tenant Resolution

### Transport authentication (AB#4315)

**Both MCP endpoints require a valid OAuth2 bearer token on the HTTP request.** `Program.cs`
registers the JWT bearer handler (`AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
.AddJwtBearer()`, configured by `ConfigureJwtBearerOptions` → Authority + ValidIssuer,
`ValidateAudience = false`) and runs `UseAuthentication()` / `UseAuthorization()` /
`UseOctoTenantAuthorization()` before mapping the transport; both `app.MapMcp(...)` calls carry
`.RequireAuthorization(McpAuthorizationPolicy.PolicyName)` — the scope policy of the next section.

Before this, the endpoints were anonymous — the `ConfigureJwtBearerOptions` configurator existed
but no scheme/middleware was ever wired, so **direct-engine (family-2/3) tools served tenant data
from MongoDB with no token at all**. `MapObservability` (health/metrics) and the file-transfer
endpoints are intentionally left anonymous — only the MCP transport is gated.

`UseOctoTenantAuthorization` (shared `TenantAuthorizationMiddleware` from octo-common-services)
validates the route `{tenantId}` against the token's `tenant_id` claim. **Client-credentials
service tokens (no user `sub` claim) are skipped by design** — that is how the AiWorker (token via
`IMcpTokenIssuer`) and the mesh-adapter `AnthropicAiQueryNode` (token via `ServiceAccountConfiguration`,
sent as `Authorization: Bearer`) reach any tenant. The tenantless `/mcp` endpoint still requires a
valid token.

> **The CC exemption is broader than those two components.** `ValidateAudience = false` means *any*
> client-credentials client of this authority passes the transport check and is then skipped here —
> see *Blast radius of the CC-token exemption (AB#5032)* below.

**The skip is staged and operator-settable — and this service now reads that setting (AB#5047).**
Since AB#5032 the middleware no longer hard-codes the exemption: identity stamps a `tenant_id` claim
on client-credentials tokens and the middleware compares it against the route tenant, governed by
`TenantAuthorizationOptions.ServiceTokenEnforcement` = `Disabled` | `LogOnly` (default) | `Enforce`.
`Program.cs` binds it with `builder.Services.AddOctoTenantAuthorization(builder.Configuration)`
(section `TenantAuthorization`), so the knob is
**`OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT`**, plus
`OCTO_TENANTAUTHORIZATION__CROSSTENANTSERVICECLIENTIDS__0…` for the cross-tenant allow-list — the
identical section and variable names Identity, Communication Controller, Asset-Repo and Bot use, so
one fleet-wide value reaches all five. Calling `UseOctoTenantAuthorization()` **without** the
`Add…` call (the state this service was in until AB#5047) leaves it on the built-in defaults, where
the environment variable is inert — the service keeps `LogOnly` while the rest of the estate moves
to `Enforce`. Semantics and rollout rules: `octo-common-services/CLAUDE.md`.

Both platform consumers that need cross-tenant reach already mint **tenant-bound** tokens
(`IMcpTokenIssuer` with `acr_values=tenant:{tenantId}`, the mesh adapter with its own
`ServiceAccountConfiguration`), so they pass the match on their own and need no allow-list entry.

The transport gate only sees the **route** tenant. The per-tool-param cross-tenant hole it leaves
open is closed by `RuntimeSecurityContextResolver` (AB#5030, next section).

### Endpoint scope policy (AB#5032)

`Configuration/McpAuthorizationPolicy` registers `McpTransportPolicy`: `RequireAuthenticatedUser()`
**plus** at least one of the scopes in `McpServiceOptions.RequiredApiScopes` on the `scope` claim.
Both `MapMcp` endpoints require it. Before this they carried a bare `RequireAuthorization()`, so a
token with no Octo API scope at all (a front-end `openid profile` token, say) reached every tool —
while every backend service gates on `scope`.

**The requirement is uniform, and it is the write scope `octo_api`.** MCP multiplexes all ~199 tools
over one JSON-RPC `POST`; the tool name lives in the request *body* and ASP.NET authorization runs on
the endpoint before the body is read, so there is no second endpoint to hang a stricter policy on and
no way to split read from write there. Accepting `octo_api.read_only` would therefore hand a
read-only token the whole write surface (`delete_tenant`, `uninstall_blueprint`, …) — strictly worse
than refusing it. Nothing breaks: the two seeded MCP clients (660…33 / 660…34) allow `octo_api` and
*not* `octo_api.read_only`, `AuthenticationTools`' device flow and `TenantTokenExchanger` both request
`octo_api`, and the mesh-adapter service account requests `ApiScopes.OctoApiFullAccess`.

`RequiredApiScopes` is configurable (`Mcp:RequiredApiScopes`) so an operator can admit a service
account provisioned with a different scope without a code change; an empty list restores the
pre-AB#5032 authenticated-only behaviour. The check accepts both wire encodings of `scope` — one
claim per scope (what the backend services' `RequireClaim` policies rely on) and a single
space-delimited value — which is never more permissive than the backend rule, it just avoids
refusing a correctly-scoped token over claim splitting.

A per-tool read/write distinction is still possible **in band**, at tool dispatch where the tool name
is known; that is a separate change from the endpoint policy. `WithHttpTransport()`'s session mode is
untouched.

### Caller identity + tenant gate for direct-engine tools (AB#5030)

Every family-2/3 tool used to open a **system session** (`tenantRepository.GetSessionAsync()`
parameterless), which bypassed data-permission enforcement (AB#4969) completely and let any
authenticated caller name any tenant in the `tenantId` tool parameter. `Services/RuntimeSecurityContextResolver.cs`
is the single choke point that fixes both:

```csharp
var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
var security = await RuntimeSecurityContextResolver.ResolveAsync(server, tenantResolution, tenantId);
if (security.Error != null)
{
    return new MyResponse { IsSuccess = false, ErrorMessage = security.Error };
}

var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
using var session = await tenantRepository.GetSessionAsync(security.SecurityContext!);
```

#### Identity comes from the request principal, never from the session store

The caller identity is read off `IHttpContextAccessor.HttpContext.User` — the principal the JWT
bearer handler produced *after* it verified signature, issuer and lifetime. `Services/McpCallerPrincipal.cs`
is the one reader of those claims (AB#5036 extracted it here so the tenant gate and the session-token
binding cannot disagree on what a "service token" is).

**It must not come from `McpSessionContext.TryGetAccessTokenAsync`.** The store is a *cache of
tokens*, not a statement about who is calling; since AB#5036 its key is derived from this very
principal, so reading an identity back out of it would be circular. It is consulted **only** on the
cross-tenant path, and there the token it hands out is bound back to the request principal before it
is used (below).

Claim reading mirrors `AssetRepositoryServices/GraphQL/Helpers.GetSecurityContext` one-for-one, so
the same user sees the same data through MCP and through GraphQL:

- **Subject** — `sub` → `ClaimTypes.NameIdentifier` → `client_id`. `ConfigureJwtBearerOptions`
  leaves `MapInboundClaims` at its ASP.NET default of `true`, so a JWT `sub` reaches the principal
  as `ClaimTypes.NameIdentifier`; probing only `"sub"` would misclassify every user token as a
  service token and hand it the CC exemption below.
- **Roles** — the union of each identity's `RoleClaimType` and the JWT short name `"role"`,
  de-duplicated. Same reason: inbound mapping may or may not have renamed them.
- **Tenant** — `tenant_id` is not in the inbound claim map, so it keeps its JWT name.

An **unauthenticated principal is a denial**, not a claimless context: `Not authenticated`, no
session opened. Same for a host that never registered `IHttpContextAccessor` (resolved with
`GetService`, so it degrades into a denial rather than an exception out of a tool).

#### The three outcomes

1. **Service (client-credentials) principal** — no subject at all, only `client_id`. Exempt from the
   tenant match, context is `ForUser(client_id, roles)`. See the blast-radius note below.
2. **User principal, `tenant_id` == resolved tenant** (the normal case) — context is
   `ForUser(sub, roles)` straight from the validated principal. **The token store is not read at
   all** on this path (pinned by `ResolveAsync_SameTenant_DoesNotConsultTheSessionTokenStore`).
3. **User principal, `tenant_id` != resolved tenant** — the cross-tenant path, below.

A user principal whose token carries **no `tenant_id` claim** is refused outright
(`…denied: the caller's token carries no 'tenant_id' claim.`). `TenantAuthorizationMiddleware`
answers 403 for exactly that shape on the route gate, and the per-tool tenant parameter must not be
more permissive than the route.

**Never `RtSecurityContext.System`** — the whole point of AB#5030 is that these tools act as the
caller. **Never throws** — every failure (unresolvable tenant, missing token, unreachable identity,
failed exchange) comes back as `RuntimeSecurityContextResult.Error` so the "never throw out of a
tool" rule holds.

#### Cross-tenant path (AB#4338)

A user homed in tenant A legitimately reaching tenant B goes through the RFC 8693 exchange. Three
things happen in order, and each one can deny:

1. **Bind the store token to the request.** The home token is fetched from the session store and
   only accepted when its `sub` **and** `tenant_id` match the request principal's. An unparseable
   (opaque) home token fails this too. Otherwise: `…denied: the stored session token does not belong
   to the authenticated caller.` Since AB#5036 this check lives in `McpSessionContext` (constant
   `Constants.SessionTokenNotBoundError`) and applies to **all three tool families**, so the resolver
   just propagates the message; client-credentials principals are exempt (see below).
2. **Exchange.** `McpSessionContext.TryGetAccessTokenAsync(server, tenantId)` performs / caches the
   exchange. A `null` result gets its **own** message — `…denied: the cross-tenant token exchange
   failed. The caller is homed in tenant 'A' and has no permission for 'B'…` — deliberately **not**
   `Not authenticated`, which would send AI clients into a pointless device-flow re-login.
3. **Verify the exchanged token.** Its `tenant_id` must read back as the target tenant, else
   `…denied: the session token is issued for tenant 'Y'.` (`<unreadable>` when opaque). Reading
   these claims without signature validation is sound: the token came straight from the identity
   server over TLS as the answer to our own exchange request and never passed through the caller.

On success the context is built from the **exchanged** token — `ForUser(shadow sub ?? client_id,
shadow roles)`. The identity in B is the B-shadow user, so reusing the A principal here would leak
A's roles into B.

#### Blast radius of the CC-token exemption (AB#5032)

The exemption is **not** limited to the AI Adapter worker (`IMcpTokenIssuer`) and the mesh-adapter
`AnthropicAiQueryNode` (`ServiceAccountConfiguration`), even though those are the components that
need it. Because `ConfigureJwtBearerOptions` sets `ValidateAudience = false`, **any**
client-credentials client of this authority passes the transport gate, and is then exempt from the
tenant gate here — it reaches every tenant as `ForUser(its client_id, its roles)`. Tightening this
(audience validation, or an allow-list of service client ids) is tracked as **AB#5032**. Until it
lands the exemption must not be removed or the AI worker loses access to every tenant it serves.

> The **endpoint** half of AB#5032 has landed (see *Endpoint scope policy* above): a client-credentials
> client now needs the `octo_api` scope to reach the transport at all, which narrows "any client of
> this authority" to "any client provisioned with the platform write scope". The tenant-gate exemption
> itself is unchanged and is being tightened separately, outside this repo.
>
> Careful with "tenant gate" — there are two. The **HTTP** one (`TenantAuthorizationMiddleware`,
> route `{tenantId}` vs. `tenant_id` claim) is the one AB#5032 staged behind
> `ServiceTokenEnforcement`, and since AB#5047 this service binds that setting, so
> `OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT=Enforce` does narrow it here. The **in-tool**
> one described in this section (`RuntimeSecurityContextResolver.ResolveTenantAccessAsync`, which
> guards the tenant named in a *tool parameter*) still exempts CC tokens unconditionally and is
> unaffected by that variable.

#### Call sites

`ResolveTenantAccessAsync` is the same call for sites that never open a session — `SchemaDiscovery
Tools`, `StreamDataMetadataTools`, `CkSchemaResources`, `StreamDataContext.TryResolveAsync`,
`EchoTool` — those only read `.Error`. It deliberately delegates to `ResolveAsync` rather than
reimplementing a cheaper gate-only variant, so the two can never drift apart.

`EchoTool` **is** gated (it used to be exempt). Ungated it was a tenant-existence oracle: any
authenticated caller could probe arbitrary tenant ids and tell "exists" from "does not exist" by the
shape of the answer. Its tenant lookup is now also wrapped in a `try/catch` — it previously threw
out of the tool on an unknown tenant.

### Stream-data CK-type permission gate (AB#5038)

`Services/DataPermissionStreamGuard` is the MCP mirror of the asset-repo GraphQL
`GraphQL/Utils/DataPermissionStreamGuard` (AB#4973, decision F4). Same three short-circuits in the
same order (system context → services not wired → `!policyTable.HasRules`), same
`RtDataAccessEvaluator.Classify(..., RtDataAction.Read, ..., includeAuditOnlyPolicies: false)`, same
rejection condition **`Denied or OwnedOnly`**, and the same message text verbatim:

```
Access denied: missing data permission 'Read' on '<SemanticVersionedFullName>' for stream data.
```

Keeping the wording identical is the point — a caller must get the same answer through MCP and
through GraphQL, and `StreamDataPermissionGateTests` pins the literal string. The one deliberate
difference is the shape: the GraphQL guard throws `ExecutionError`; MCP tools never throw, so the
guard **returns** the message and the call site puts it into `ErrorMessage`.

An **owner-scoped grant denies** stream reads. Time-series rows carry no creator, so row-level
filtering is impossible for them — the conservative accepted limitation F4 of AB#4969. AuditOnly
policies never block.

Call sites:

- `StreamDataContext.TryResolveAsync` — the shared archive-resolution path, so all four transient
  query tools are covered at once. It now calls `ResolveAsync` (not `ResolveTenantAccessAsync`)
  because it needs the caller's `RtSecurityContext`, not just the deny decision.
- `execute_stream_data_query` — guards on the persisted `QueryCkTypeId`, mirroring asset-repo's
  `StreamDataQuery` resolver (which likewise does *not* read it off an archive snapshot).
- `StreamDataMetadataTools` — `get_archive_storage_stats` (row counts and table sizes describe the
  rows), `get_rollup_query_metadata` (the logical source paths describe the CK attributes) and
  `resolve_series_query` (which archive holds the series at which grain).

Two shape notes on the metadata tools. `get_archive_storage_stats` is **fail-closed on the batch**: a
single protected archive refuses the whole call rather than blanking one row, because the response is
index-aligned with the caller's input list and a blanked row is indistinguishable from "table does not
exist yet". And it only resolves archive snapshots when `DataPermissionStreamGuard.IsEnforcingAsync`
says the tenant has policies — on the (overwhelmingly common) unprotected tenant it stays the single
`GetArchiveStatsAsync` round-trip it has always been. `IsEnforcingAsync` is cheap to pair with the
follow-up check because `IDataPermissionResolver` TTL-caches the policy table per tenant (60 s).

The guard loads the tenant CK cache (`LoadCacheForTenantAsync`) before classifying, but **only on the
protected path**: `RtDataPermissionCkTypeHelper.GetSelfAndBaseFullNames` can only walk the base-type
chain from a hydrated cache, and a cold cache would silently under-block a policy that targets a base
type. The GraphQL side never needs this because its schema was built from the same cache.

**Known gap — family-3 archive stores.** The engine-side stores
(`ITenantContext.GetStreamDataRepository()`, `GetArchiveRuntimeStore()`,
`GetRollupArchiveRuntimeStore()`) open their **own system sessions** internally, so the caller's
`RtSecurityContext` never reaches the archive read and *row-level* data-permission enforcement does
not happen on this path. Threading the security context into those stores is the larger fix and is
deliberately not part of AB#5038. Until it lands, the AB#5030 tenant gate plus this CK-type gate are
the whole barrier — which is exactly the position the GraphQL surface is in, so the two remain
equivalent.

#### Testing

`TestBase` installs a `DefaultHttpContext` (exposed as `TestHttpContext`) behind a registered
`IHttpContextAccessor`, and seeds it with an authenticated user principal —
`tenant_id=test-tenant`, `sub=test-subject`, `role=TestRole` — so family-2/3 tests have a caller by
default. Helpers: `GivenAuthenticatedCaller`, `GivenUnauthenticatedCaller`,
`GivenServicePrincipalCaller`, `GivenCallerWithoutTenantClaim`, `GivenForeignTenantCall`,
`GivenSuccessfulTenantExchange`, `GivenFailingTenantExchange`, `GivenTokenExchange`, plus
`GivenRuntimeCallerToken` / `GivenNoRuntimeCallerToken` for the store on the cross-tenant path.
`BuildPrincipal` emits claims in the shape the bearer handler really produces (`sub` as
`ClaimTypes.NameIdentifier`, `role` as `ClaimTypes.Role`, `tenant_id`/`client_id` unmapped);
`TestJwt.CreateFull(tenantId, subjectId, clientId, roles)` builds the matching JWTs.

**`MockTenantRepository` is cast `.As<ISecureSessionFactory>()`** (`MockSecureSessionFactory`) and
its **parameterless `GetSessionAsync()` throws**. Both matter:
`TenantRepositorySecurityExtensions.GetSessionAsync(ctx)` falls back to the parameterless system
session **silently** for a repository that does not implement `ISecureSessionFactory` — so a mock
without that face makes every call site look correct while enforcing nothing. Verify against
`MockSecureSessionFactory` to assert which `RtSecurityContext` a tool actually opened its session
with. `RuntimeTenantGateTests.SessionOpeningCallSites` is the parametrised proof for all 13
session-opening sites (8 CRUD tools, 3 aggregation tools over 2 sites, `execute_stream_data_query`,
`KnowledgeResources`); `update_entity`'s second post-commit `readSession` needs its own arrangement
and has a dedicated test.

### In-band session token (outbound calls) — bound to the caller (AB#5036)

Separate from the inbound gate, family-1 tools use a per-session token for their **outbound** calls
to the backend services:

1. **OAuth Device Authorization** — `authenticate` issues a device code; user logs in via browser;
   `check_auth_status` polls until tokens are issued; tokens go into `IMcpSessionTokenStore`.
2. **Per-request token injection** — `McpSessionContext.ResolveAccessTokenAsync(server[, tenantId])`
   picks the token; the `*ClientContext` helpers feed it to
   `OctoServiceClientFactory.Create*Client(tenantId, accessToken)`.

This store decides **which identity Identity / Asset-Repo / Communication / Reporting / StreamData /
Bot see**, so it is bound to the authenticated caller in two independent ways. Both live in
`Services/McpSessionContext.cs`.

**1. The key is derived from the request principal, not from the client.**
`McpSessionContext.TryGetSessionKey` returns `u:{tenant_id}:{sub}` for a user principal, `c:{client_id}`
for a client-credentials one, optionally suffixed `|{Mcp-Session-Id}` when the client sent that header.
Consequences:

- **No shared slot.** The old key was the `Mcp-Session-Id` header alone with a constant
  `"default-session"` fallback. Since `WithHttpTransport()` runs at its default
  `HttpServerSessionMode.Stateless`, the server never mints that header and no client sends one — so in
  practice *every* caller on the pod shared one process-wide slot, and whoever ran `authenticate` last
  donated their identity (cross-tenant included) to everyone else.
- **The header is no longer an identity.** A caller may still send any value; it only partitions
  *within* their own namespace, so it can never address someone else's entry.
- **No principal ⇒ no store.** `TryGetSessionKey` returns null and the call falls back to the request's
  own `Authorization: Bearer` — which is exactly the right identity. `authenticate` /
  `check_auth_status` refuse outright in that case (there is no slot to park a device code in that
  isn't shared). Since AB#4315 both endpoints require a validated bearer, so a principal is always
  present in production. `McpSessionContext.GetCallerLabel` is the deliberately non-null variant, used
  **only** for the file-transfer ownership tag (transfers authorise on the opaque 128-bit transfer id,
  not on the session).

**2. A stored token is verified before it is used.** `VerifyBinding` requires the token's `sub` **and**
`tenant_id` to equal the request principal's; an opaque token fails because it cannot be bound at all.
The failure is a hard refusal with `Constants.SessionTokenNotBoundError` (`…does not belong to the
authenticated caller…`), *not* a silent downgrade to the header bearer and *not* `Not authenticated` —
the caller is authenticated, their stored session simply carries a different identity. The check is
re-applied to a refreshed token, so an expired foreign token cannot be laundered into a fresh one.

> **Client-credentials principals are exempt from check 2.** A service token has no `sub`, so the
> subject match is not a question that can be asked of it. The AI Adapter worker (`IMcpTokenIssuer`)
> and the mesh-adapter `AnthropicAiQueryNode` (`ServiceAccountConfiguration`) present their token in
> the `Authorization` header and never populate the store; requiring a subject would lock both out of
> every tenant. Their store namespace is their `client_id`. Same exemption, same reason as the tenant
> gate — tightening it is AB#5032.

**Device flow under the binding.** `authenticate` and `check_auth_status` land on the same key because
the principal is the same across the two calls. But a device login against a **different** tenant
produces a token for that tenant's shadow user, which check 2 then refuses — `check_auth_status` says
so in its success message instead of letting the next tool call fail with an unrelated-looking error.
The sanctioned way to reach another tenant is `switch_tenant` / the transparent RFC 8693 exchange.

**Not changed:** `WithHttpTransport()` still runs in the default stateless mode. Switching it to
`Stateful` would make the transport mint and bind `Mcp-Session-Id` itself, which is a behaviour change
for every client (session affinity required, GET/DELETE endpoints appear) — evaluate separately.

Tenant comes from (in order):
1. Explicit `tenantId` tool parameter
2. Route parameter `{tenantId}` on the `/{tenantId}/mcp` endpoint
3. Error from `ITenantResolutionService.ResolveTenantId(...)`

Never store tenant state on the session. Stateless multi-tenancy is the design.

### Cross-tenant token exchange (AB#4338)

The backend `TenantAuthorizationMiddleware` authorizes the route tenant strictly against the token's
`tenant_id` claim (NOT `allowed_tenants`), so one access token acts on exactly one tenant. To operate
on a different tenant B without a device re-auth, the five **tenant-routed** `*ClientContext` helpers
call `McpSessionContext.TryGetAccessTokenAsync(server, tenantId)`, which transparently exchanges the
home token for a B-scoped token (RFC 8693 token-exchange grant → `POST /connect/token`
`grant_type=urn:ietf:params:oauth:grant-type:token-exchange`, `subject_token`=home token,
`acr_values=tenant:B`, `client_id=octo-mcpServices-device`) via `ITenantTokenExchanger`, cached
per-`(sessionKey, tenantId)` in `McpSessionTokenStore` — the same caller-bound `sessionKey` as the home
token (AB#5036), so a cached B token is reachable only by the principal that obtained it, and the home
token used as `subject_token` has already passed the binding check. The identity side re-resolves roles in B (issues
the token for the B-shadow user) so there is no role leak. The `switch_tenant` tool is the explicit
affordance; on failure it recommends the `authenticate` device-flow fallback.

**Opaque-token safety:** the overload exchanges ONLY when the home token's `tenant_id` is readable AND
differs from the target. Opaque/service tokens (adapter/worker, no readable `tenant_id`) keep using the
home token — service tokens are skipped by `TenantAuthorizationMiddleware` anyway. **Bot stays on the
home token** — its client is NOT tenant-routed (`CreateBotClient` takes no `tenantId`), so an exchange
there is unnecessary and could break bot ops the home token already serves.

> **Interactive-client note:** because the transport now requires an inbound bearer, a purely
> interactive client that previously relied only on the in-band device flow must present a bearer
> token to connect. The production clients (AiWorker, mesh-adapter) already do. The
> `ConfigureJwtBearerOptions` contract is pinned by `Configuration/ConfigureJwtBearerOptionsTests`;
> the endpoint-gating itself has no in-process HTTP test (the host needs MongoDB/RabbitMQ) — verify
> against a running identity service.

## Outbound fetches: the knowledge-source URL policy (AB#5037)

`KnowledgeResources` materialises `AiKnowledgeSource` entities. The `Url` kind used to be fetched with
a plain `httpClientFactory.CreateClient("knowledge-fetch").GetAsync(storedUrl)` — no scheme check, no
host check, unbounded body, auto-following redirects. The URL is **tenant-writable** data and the
request leaves from **inside the cluster**, with the response body handed back to the caller: that is
a full SSRF primitive (cloud metadata endpoints, cluster-internal services, exotic schemes).

The fetch now goes through `Services/KnowledgeUrlFetcher` (`IKnowledgeUrlFetcher`) and
`Services/KnowledgeUrlPolicy`, configured by `Options/KnowledgeFetchOptions` (section
`KnowledgeFetch`, env `OCTO_KNOWLEDGEFETCH__…`):

- **Scheme allow-list** — `http` / `https` only. (Note: on Unix an absolute *path* like `/etc/passwd`
  parses as an absolute `file://` URI, so it is caught here rather than by the absolute-URL check.)
- **Address ranges blocked after DNS resolution**, not before. Checking the literal host is useless:
  any name — including one the tenant controls — can point at `169.254.169.254`. `IHostAddressResolver`
  (prod: `SystemDnsResolver`) resolves the host and **every** returned address must be publicly
  routable; one blocked record in a multi-record answer refuses the whole fetch, because which record
  the connection would pick is not ours to decide. Blocked: loopback, `0/8`, RFC 1918, RFC 6598 CGNAT,
  link-local incl. the IPv4/IPv6 metadata endpoints, multicast/broadcast, IPv6 loopback / link-local /
  unique-local, and the IPv4-mapped + IPv4-compatible IPv6 forms of all of them. An IP literal skips
  DNS but not the range check. An unresolvable host is a refusal (fail closed).
- **Size limit** — `MaxResponseBytes` (default 1 MiB). A declared `Content-Length` over budget is
  refused before streaming; an undeclared oversized body is read to the budget and reported as
  `Truncated` (a partial CLAUDE.md fragment is still useful, so truncation is not an error).
- **Time limit** — `TimeoutSeconds` (default 10) as **one** budget for the whole exchange including
  redirects. The named `knowledge-fetch` client is registered with `Timeout.InfiniteTimeSpan` on
  purpose: a per-request timeout would multiply with the hop count.
- **Redirects are followed by hand**, up to `MaxRedirects` (default 3), with the primary handler at
  `AllowAutoRedirect = false`. Auto-redirect would connect to the target before anything could inspect
  it — exactly the hole the policy exists to close. Every `Location` is re-validated against the full
  policy, so a public host cannot bounce the request into the cluster.
- **Operator escape hatches** — `AllowedHosts` (exact, or a leading-dot suffix entry like
  `.internal.example.com`) waives **only** the address-range check for named internal sources; scheme,
  size, time and the per-hop redirect re-check still apply, and a redirect target must be allow-listed
  in its own right. `AllowPrivateNetworks` disables the address check wholesale for operators who
  terminate egress in a proxy. Both default to off/empty.

**There is no unguarded fallback.** `KnowledgeResources` resolves `IKnowledgeUrlFetcher` with
`GetService`; when it is not registered the source renders as "fetcher is not configured", it is never
fetched raw. Don't reintroduce a direct `HttpClient` call on this path.

## Test Infrastructure

`tests/McpServices.Tests/` uses xUnit + Moq + FluentAssertions.

- `TestBase` — base mocks (`McpServer`, `TestServiceProvider`, `IOctoHttpContextAccessor`, `ITenantResolutionService`, `ICkCacheService`, `ITenantRepository` + its `ISecureSessionFactory` face) plus a `DefaultHttpContext` carrying the authenticated request principal the direct-engine tools derive their caller identity from, and an `IMcpSessionTokenStore` holding the matching home token for the cross-tenant exchange path (AB#5030 — see *Caller identity + tenant gate* above). The parameterless `GetSessionAsync()` throws on purpose.
- `ToolTestBase : TestBase` — adds `IMcpSessionTokenStore` + `IOctoServiceClientFactory` mocks plus 6 per-SDK-client mocks (`MockIdentityClient`, `MockAssetClient`, `MockCommunicationClient`, `MockStreamDataClient`, `MockReportingClient`, `MockBotClient`) and the real `FileTransferStore`. Helpers: `GivenAuthenticated()`, `GivenUnauthenticated()`, `GivenTokenExpired()`.
  - **`GivenAuthenticated()` returns the token it stored** and defaults to a JWT bound to the request principal `TestBase` installs (`sub=test-subject`, `tenant_id=test-tenant`). Since AB#5036 an opaque placeholder would be refused by the binding check, so assert against the returned value instead of a literal. Passing an explicit token is still allowed — for a token belonging to another identity (a denial test) or when the caller is a service principal.
  - `MockTokenExchanger` defaults to an **identity-preserving** exchange (returns the `subject_token` back) because the default session token now carries a readable `tenant_id`: a test that points a tool at another tenant takes the AB#4338 exchange path and still sees the token it arranged. Tests about the exchange itself override this setup.
- `InternalsVisibleTo("McpServices.Tests")` is set on `McpServices.csproj` so tests can access `FileTransferStore` directly (the interface is `IFileTransferStore`).

### Adding a tests file

```csharp
public class MyToolsTests : ToolTestBase
{
    public MyToolsTests() { GivenAuthenticated(); }

    [Fact]
    public async Task MyTool_HappyPath_CallsSdk()
    {
        MockIdentityClient.Setup(c => c.DoSomething("x")).ReturnsAsync(new SomeDto());

        var result = await MyTools.MyTool(MockServer.Object, "x");

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.DoSomething("x"), Times.Once);
    }

    [Fact]
    public async Task MyTool_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await MyTools.MyTool(MockServer.Object, "x");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.DoSomething(It.IsAny<string>()), Times.Never);
    }
}
```

### Pitfalls to remember

- **CkTypeId format is `Name-VersionUint`, not SemVer.** `new CkTypeId("MyType-1")` works; `new CkTypeId("MyType-1.0.0")` throws because the SDK reflection-constructs `CkTypeId` from the second path segment and parses the version as `uint`.
- **`OctoObjectId` must be a 24-char hex string.** Use realistic values like `"507f1f77bcf86cd799439011"` in tests.
- **Moq method matchers must use the right type-param.** For methods that take `IEnumerable<T>`, match with `It.IsAny<IEnumerable<T>>()`, not `It.IsAny<List<T>>()`.

### CI: tests in Azure Pipelines

`devops-build/azure-pipelines.yml` runs the full test suite on every push to `main`, `dev/*` and `test/*` branches. The relevant step:

```yaml
- task: DotNetCoreCLI@2
  displayName: 'Test (unit + integration)'
  inputs:
    command: 'test'
    arguments: '--configuration $(buildConfiguration) /p:OctoNugetPrivateServer=$(nugetPrivateServer) --logger "console;verbosity=detailed" --collect:"XPlat Code Coverage"'
    projects: |
      **/*Tests.csproj
      !**/*SystemTests.csproj
    testRunTitle: 'McpServices CI - $(Build.BuildNumber)'
    publishTestResults: true
- task: PublishCodeCoverageResults@2
  displayName: 'Publish code coverage'
  condition: succeededOrFailed()
  inputs:
    summaryFileLocation: '$(Agent.TempDirectory)/**/coverage.cobertura.xml'
```

Notes:

- **Test config is `Release`** (not `DebugL`). The test step uses `$(buildConfiguration) = Release` and the published NuGet packages from `$(nugetPrivateServer)` — local-only DebugL packages from `../nuget/` are not available on the agent. Mirror this locally with `dotnet test Octo.McpServices.sln -c Release` when you suspect a config-sensitive break.
- **Results land in the Azure DevOps Tests tab** under run title `McpServices CI - <buildNumber>`. Failed tests show stack traces and console output thanks to the `console;verbosity=detailed` logger.
- **Code coverage** is collected via `coverlet.collector` (already referenced in `McpServices.Tests.csproj`) and surfaced in the Code Coverage tab of the build. Cobertura XML lands in `$(Agent.TempDirectory)`.
- **Test glob excludes `*SystemTests.csproj`** so a future `McpServices.SystemTests` project (real-service integration suite) can be added later without breaking the main build — those would need their own pipeline + Testcontainers env, matching the pattern in `octo-identity-services`.

The current suite is ~839 mock-based unit tests + a handful of in-process integration tests (`McpServerIntegrationTests`). If you add real-service-dependent tests, put them in a separate `*SystemTests` project so they're skipped here.

## Project Layout

```
src/McpServices/
├── Program.cs                          # Composition root + endpoint mapping
├── appsettings.json                    # Includes OctoServiceUrls section
├── Options/
│   ├── McpServiceOptions.cs            # MCP-server-specific options (incl. RequiredApiScopes, AB#5032)
│   ├── KnowledgeFetchOptions.cs        # Knowledge-source fetch policy (AB#5037)
│   └── OctoServiceUrlOptions.cs        # Backend service URLs
├── Configuration/
│   └── McpAuthorizationPolicy.cs       # McpTransportPolicy scope requirement (AB#5032)
├── Services/
│   ├── IOctoServiceClientFactory.cs    # SDK client factory interface
│   ├── OctoServiceClientFactory.cs     # Builds per-tenant SDK clients
│   ├── McpCallerPrincipal.cs           # The validated request principal (single claim reader)
│   ├── DataPermissionStreamGuard.cs    # Stream-data CK-type Read gate (AB#5038, mirrors asset-repo)
│   ├── KnowledgeUrlPolicy.cs           # SSRF policy + IHostAddressResolver (AB#5037)
│   ├── KnowledgeUrlFetcher.cs          # Guarded knowledge-source fetch (AB#5037)
│   ├── McpSessionContext.cs            # Caller-bound store key + access token resolution (AB#5036)
│   ├── McpSessionTokenStore.cs         # OAuth tokens keyed by the caller-bound session key
│   ├── TenantResolutionService.cs      # tool param / route param resolution
│   ├── {Identity,Asset,Communication,StreamData,Reporting,Bot}ClientContext.cs
│   ├── IFileTransferStore.cs           # File transfer abstraction
│   ├── FileTransferStore.cs            # Disk-backed + sweeper
│   ├── JobPollingHelper.cs             # Async-job polling for asset/bot jobs
│   ├── AggregationMapper.cs            # Lowercase enum → engine + validation (family 3)
│   ├── DynamicToolService.cs           # Generic CK CRUD discovery (legacy family 2)
│   └── ToolExecutionService.cs         # Tool stats (legacy family 2)
├── Routing/
│   ├── TenantIdRouteConstraint.cs      # MCP /{tenantId}/mcp routing
│   └── FileTransferController.cs       # PUT/GET /file-transfer/{upload,download}/{id}
├── Models/                             # Response envelope DTOs grouped by domain
│   ├── TenantManagementResponses.cs
│   ├── IdentityResponses.cs
│   ├── IdentityLongTailResponses.cs
│   ├── AssetResponses.cs
│   ├── CommunicationResponses.cs
│   ├── TimeSeriesResponses.cs
│   ├── FileTransferResponses.cs
│   └── Aggregation/                    # Family 3 — lowercase function enum, alias rules, response shapes
│       ├── AggregationFunctionDto.cs   # count/sum/avg/min/max — DON'T fix to PascalCase
│       ├── AggregationColumnDto.cs     # { Function, AttributePath?, Alias? }
│       ├── SortColumnDto.cs            # asc/desc
│       └── AggregationResponses.cs     # AggregationResultResponse + Stream/Downsampling/Stats/RollupMeta
└── Tools/                              # MCP tool classes
    ├── AuthenticationTools.cs          # OAuth device flow
    ├── IdentityTools.cs                # whoami, list_tenants
    ├── TenantManagementTools.cs
    ├── UserManagementTools.cs / RoleManagementTools.cs / GroupManagementTools.cs
    ├── ClientManagementTools.cs / IdentityProviderTools.cs
    ├── ApiResourceTools.cs / ApiScopeTools.cs / ApiSecretTools.cs
    ├── EmailDomainGroupRuleTools.cs / ExternalTenantUserMappingTools.cs / AdminProvisioningTools.cs
    ├── BlueprintTools.cs / CkModelLibraryTools.cs
    ├── CommunicationLifecycleTools.cs / AdapterTools.cs / PipelineTools.cs
    ├── DataFlowTriggerPoolTools.cs / WorkloadTools.cs
    ├── TimeSeriesTools.cs / ReportingTools.cs / DiagnosticsTools.cs
    ├── FileTransferTools.cs / CkModelFileTools.cs / TenantBackupTools.cs
    ├── RuntimeEntityCrudTools.cs / SchemaDiscoveryTools.cs   # Generic CK CRUD (family 2)
    ├── RuntimeAggregationTools.cs                            # Aggregations (family 3)
    ├── StreamDataAggregationTools.cs                         # 4 stream-data query variants (family 3)
    ├── StreamDataMetadataTools.cs                            # storage_stats + rollup_query_metadata (family 3)
    ├── ToolManagementTools.cs / EchoTool.cs
tests/McpServices.Tests/
├── ToolTestBase.cs                     # Adds SDK client + file-store mocks
├── TestBase.cs                         # Lower-level base
├── Services/                           # Factory + Context + Store tests
└── Tools/                              # One file per Tools/ class
```

## Things NOT to do

- **Don't bypass `*ClientContext` helpers.** Even if you only need one tenant for one call, go through them — they enforce auth + tenant resolution + factory routing uniformly.
- **Don't add a tool without tests.** The "I'll add tests later" pattern hasn't held up in this codebase; every commit landed with its tests in the same commit.
- **Don't accept base64-encoded file content as a tool parameter.** Use the file-transfer endpoints. They handle multi-GB files and stream from disk; base64 in JSON-RPC blows up token budgets and memory.
- **Don't downgrade `confirm: true` to a default-true.** AI clients should opt into destructive actions explicitly.
- **Don't write to `Console`** or rely on `ILogger` for user-visible output. Use the `Message` / `ErrorMessage` fields of the response envelope.
- **Don't share SDK clients across requests.** Per-tenant `ServiceUri` caching makes this unsafe.
- **Don't manually parse JWT tokens** outside `IdentityTools.cs`. Use the existing pattern (`JwtSecurityTokenHandler`) — or better, lift it into a helper if a third call site appears.

## Adding Tools — Step-by-Step Checklist

1. Find the equivalent `octo-cli` command in `octo-cli/src/ManagementTool/Commands/Implementations/**`. Note: SDK method signature, required args, destructive flag.
2. Decide which `*ClientContext` to use based on which SDK client the CLI uses.
3. If a response payload is non-trivial, add a wrapper DTO in `src/McpServices/Models/<domain>Responses.cs`.
4. Write the tool method following the signature pattern above.
5. If you needed a new SDK client (e.g. Bot), update `IOctoServiceClientFactory` + `OctoServiceClientFactory` + `OctoServiceUrlOptions` + `ToolTestBase`.
6. Write tests: happy path + unauthenticated + missing args + (if destructive) confirm-required.
7. `dotnet test Octo.McpServices.sln -c DebugL` — all green before commit.
8. Update `README.md` Available Tools section if you added a new category.

## Background — Why the codebase looks like this

The MCP server was originally a thin runtime CRUD proxy (Versions 1.0–1.1). Versions 1.2–1.3 added the full `octo-cli` command surface via the SDK service clients, plus out-of-band file transfer. Version 1.4 added aggregation + stream-data query parity with the asset-repo GraphQL transient-query API. Three families of tools coexist on purpose:

- **Family 1** talks HTTP to the backend services via `OctoServiceClientFactory` + `*ClientContext` helpers — same code path the CLI uses, so the orchestrated workflows (tenant create + admin provision, blueprint update, workload deploy through pool, etc.) work identically.
- **Family 2** talks directly to `ITenantRepository` (MongoDB) — fast generic CRUD and schema discovery, no platform-admin operations, no HTTP overhead.
- **Family 3** also talks directly to the engine (via `ITenantRepository` for runtime aggregations; via `ITenantContext.GetStreamDataRepository()` for stream-data queries), with its own lowercase enum + `AggregationMapper` conventions. Mirrors the asset-repo GraphQL transient-query surface so the AI never has to construct GraphQL.

Don't try to merge them. Generic CRUD doesn't go through the service clients (no HTTP overhead for read-heavy entity queries); platform-admin operations don't bypass the service clients (skipping them would skip the orchestration); aggregations don't go through `*ClientContext` (they need direct engine access for `RtEntityQueryOptions` configuration). The three layers have different cost profiles and different validation needs.
