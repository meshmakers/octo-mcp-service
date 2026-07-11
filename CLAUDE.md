# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

`octo-mcp-service` is the **Model Context Protocol** server for OctoMesh. It exposes ~170 tools that mirror the full `octo-cli` command surface plus generic CK-type CRUD, so AI assistants can administer the platform end-to-end without invoking the CLI.

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

# Run all tests (currently 400, ~250 ms)
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

The current ratio is ~2.4 tests per tool (400 tests for 166 tools). Don't lower it.

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
`.RequireAuthorization()`.

Before this, the endpoints were anonymous — the `ConfigureJwtBearerOptions` configurator existed
but no scheme/middleware was ever wired, so **direct-engine (family-2/3) tools served tenant data
from MongoDB with no token at all**. `MapObservability` (health/metrics) and the file-transfer
endpoints are intentionally left anonymous — only the MCP transport is gated.

`UseOctoTenantAuthorization` (shared `TenantAuthorizationMiddleware` from octo-common-services)
validates the route `{tenantId}` against the token's `tenant_id` claim. **Client-credentials
service tokens (no user `sub` claim) are skipped by design** — that is how the AiWorker (token via
`IMcpTokenIssuer`) and the mesh-adapter `AnthropicAiQueryNode` (token via `ServiceAccountConfiguration`,
sent as `Authorization: Bearer`) reach any tenant. The tenantless `/mcp` endpoint still requires a
valid token; per-tool-param cross-tenant access on a user token scoped to a different tenant is a
secondary hardening not covered here.

### In-band session token (outbound calls)

Separate from the inbound gate, family-1 tools use a per-session token for their **outbound** calls
to the backend services:

1. **OAuth Device Authorization** — `authenticate` tool issues a device code; user logs in via browser; `check_auth_status` polls until tokens are issued; tokens go into `IMcpSessionTokenStore` keyed by the MCP session id from the `Mcp-Session-Id` HTTP header.
2. **Per-request token injection** — `McpSessionContext.TryGetAccessToken(server)` pulls the current session's access token; the `*ClientContext` helpers feed it to `OctoServiceClientFactory.Create*Client(tenantId, accessToken)`.

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
per-`(sessionId, tenantId)` in `McpSessionTokenStore`. The identity side re-resolves roles in B (issues
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

## Test Infrastructure

`tests/McpServices.Tests/` uses xUnit + Moq + FluentAssertions.

- `TestBase` — base mocks (`McpServer`, `TestServiceProvider`, `IOctoHttpContextAccessor`, `ITenantResolutionService`, `ICkCacheService`, `ITenantRepository`).
- `ToolTestBase : TestBase` — adds `IMcpSessionTokenStore` + `IOctoServiceClientFactory` mocks plus 6 per-SDK-client mocks (`MockIdentityClient`, `MockAssetClient`, `MockCommunicationClient`, `MockStreamDataClient`, `MockReportingClient`, `MockBotClient`) and the real `FileTransferStore`. Helpers: `GivenAuthenticated()`, `GivenUnauthenticated()`, `GivenTokenExpired()`.
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

The current suite is ~400 mock-based unit tests + a handful of in-process integration tests (`McpServerIntegrationTests`). If you add real-service-dependent tests, put them in a separate `*SystemTests` project so they're skipped here.

## Project Layout

```
src/McpServices/
├── Program.cs                          # Composition root + endpoint mapping
├── appsettings.json                    # Includes OctoServiceUrls section
├── Options/
│   ├── McpServiceOptions.cs            # MCP-server-specific options
│   └── OctoServiceUrlOptions.cs        # Backend service URLs
├── Services/
│   ├── IOctoServiceClientFactory.cs    # SDK client factory interface
│   ├── OctoServiceClientFactory.cs     # Builds per-tenant SDK clients
│   ├── McpSessionContext.cs            # Session id + access token helpers
│   ├── McpSessionTokenStore.cs         # OAuth tokens keyed by session id
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
