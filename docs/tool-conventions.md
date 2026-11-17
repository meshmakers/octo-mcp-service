---
description: Rules for every MCP tool: tests, ClientContext helpers, signature, response envelope, confirm gate, risk level, locking, new-tool checklist.
applies_to: src/McpServices/**, src/McpServices.Resources/**, tests/McpServices.Tests/Tools/**
---

# Tool conventions

## 1. Every new tool MUST have unit tests

Minimum coverage per tool:

- **Happy path** — mock the SDK client, return realistic DTO, assert the tool returned `IsSuccess = true` and called the right SDK method with the right arguments.
- **Unauthenticated** — `GivenUnauthenticated()`, assert `IsSuccess = false` and `ErrorMessage` contains `"Not authenticated"`. No SDK call.
- **Missing required args** — pass empty / null, assert validation error, no SDK call.
- **Destructive without confirm** — for any tool with a `confirm` parameter, assert refusing without it.

Keep the tests-per-tool ratio of the existing tools; don't lower it.

## 2. Use the `*ClientContext` helpers — never call the factory directly from a tool

Every SDK-backed tool starts the same way:

```csharp
var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
if (ctx.Error != null)
{
    return new MyResponse { IsSuccess = false, ErrorMessage = ctx.Error };
}

// ctx.Client is the IIdentityServicesClient, ctx.TenantId is the resolved tenant
```

Seven context helpers exist in `src/McpServices/Services/`:

| Context | Backing SDK Client | Tenant routing |
|---|---|---|
| `IdentityClientContext` | `IIdentityServicesClient` | per-tenant (`{tenantId}/v1`) |
| `AssetClientContext` | `IAssetServicesClient` | per-tenant |
| `CommunicationClientContext` | `ICommunicationServicesClient` | per-tenant (`{tenantId}/v1`) — no system fallback |
| `StreamDataClientContext` | `IStreamDataServicesClient` | per-tenant (`{tenantId}/v1`) — was `api/v1` |
| `ReportingClientContext` | `IReportingServicesClient` | per-tenant (`{tenantId}/v1`) — no system fallback |
| `BotClientContext` | `IBotServicesClient` | system-scoped |
| `AiClientContext` | `IAiServicesClient` | per-tenant |

## 3. Tool method signature pattern

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

## 4. Response envelope

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

## 5. Destructive operations require `confirm: true`

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

## 6. SDK DTOs go on the wire as-is

The MCP framework serialises whatever you return. Returning SDK DTOs (`UserDto`, `ClientDto`, `BlueprintApplyResultDto`, etc.) directly is the convention — no MCP-side translation layer. If the SDK changes a DTO shape, the MCP response changes with it, and that's intentional.

For composite responses (list + count + tenant id), define your own wrapper DTO in `src/McpServices/Models/*Responses.cs`. Group by domain (`IdentityResponses.cs`, `AssetResponses.cs`, etc.) — don't make one file per response type.

## 7. Per-request SDK clients (never singleton)

The SDK clients cache their `ServiceUri` on first use. Sharing one client across multiple tenants → wrong tenant in the URL on the second call. **Always** go through `IOctoServiceClientFactory.Create*Client(tenantId, accessToken)` — it returns a fresh instance.

The `*ClientContext.TryBuild` helpers handle this for you. Don't manually construct SDK clients in tool code.

## 7a. Never build a JSON Schema into the global registry

JsonSchema.Net registers every schema built with default options (`JsonSchema.FromText(json)`, `JsonSchema.Build(...)`)
in the process-wide `SchemaRegistry.Global`. A schema without `$id` gets a new random base URI per build, so a schema
built per request stays in memory for the life of the pod — `validate_pipeline_definition` leaked the adapter's whole
composite schema on every call this way and the pod was OOM-killed. Build request-scoped schemas with
`PipelineSchemaCompiler.Compile` (a per-call `SchemaRegistry`, which still falls back to the global one for the
meta-schemas). Guard: `tests/McpServices.Tests/Services/PipelineSchemaCompilerTests.cs`.

## 8. Risk classification (`[McpRisk]`) for AI Adapter approval gating

Tools have an optional `[McpRisk(McpRiskLevel.Low|Medium|High)]` attribute that classifies their blast radius. The AI Adapter worker calls `get_tool_risk_metadata` once at session start and uses the result to decide whether a tool call needs to be routed through its user-facing approval gate before running.

**This is not authorisation.** Family-1 tools delegate authorisation to the backend services via the propagated OAuth token; family-2/3 tools authorise in the tool through `RuntimeSecurityContextResolver` and the caller's data permissions (`tenant-gate.md`). `McpRisk` is informational metadata that the worker reads to drive its own safety story.

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

## 9. Optimistic locking on `update_entity` / `delete_entity`

Concurrent AI sessions can write to the same runtime entity. `update_entity` and `delete_entity` accept an optional `expected_version` (the `RtVersion` the caller observed on its prior read):

- **Omitted** → last-write-wins, identical to the behaviour before optimistic locking. `update_entity` still bumps `RtVersion` on the way out so a later optimistic call sees a meaningful token.
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

## Adding Tools — Step-by-Step Checklist

1. Find the equivalent `octo-cli` command in `octo-cli/src/ManagementTool/Commands/Implementations/**`. Note: SDK method signature, required args, destructive flag.
2. Decide which `*ClientContext` to use based on which SDK client the CLI uses.
3. If a response payload is non-trivial, add a wrapper DTO in `src/McpServices/Models/<domain>Responses.cs`.
4. Write the tool method following the signature pattern above.
5. If you needed a new SDK client (e.g. Bot), update `IOctoServiceClientFactory` + `OctoServiceClientFactory` + `OctoServiceUrlOptions` + `ToolTestBase`.
6. Write tests: happy path + unauthenticated + missing args + (if destructive) confirm-required.
7. Run the tests and update the `README.md` Available Tools section as `AGENTS.md` ("Before you commit") says.
