# AGENTS.md

Instructions for coding agents in this repository. Detail lives in `docs/`; read the matching file before changing
the code it covers.

## What this repo is

`octo-mcp-service` is the **Model Context Protocol** server for OctoMesh. It exposes the tools that mirror the full
`octo-cli` command surface plus generic CK-type CRUD, so AI assistants can administer the platform end-to-end without
invoking the CLI.

Three distinct tool families live here — be aware which one you're touching:

1. **Platform-admin tools** — thin wrappers over the `Meshmakers.Octo.Sdk.ServiceClient` SDK. One tool per `octo-cli`
   command. These tools talk HTTP to the Identity / Asset / Communication / Reporting / StreamData / Bot services.
2. **Generic CK CRUD + schema tools** — predate the platform-admin tools and talk directly to the runtime engine
   (MongoDB) via `ITenantRepository`. These do not use the SDK service clients.
3. **Aggregation + stream-data query tools** — newer; mirror the asset-repo GraphQL transient-query surface. They share
   family 2's path (talk to the engine directly via `ITenantRepository` / `ITenantContext.GetStreamDataRepository`),
   but use the lowercase `AggregationFunctionDto` enum and the `AggregationMapper` helper — *not* the platform-admin
   `*ClientContext` pattern.

If you're adding a tool that mirrors an `octo-cli` command, you're in family 1 — follow the `*ClientContext` pattern
of `docs/tool-conventions.md`. If you're adding a runtime/stream-data read or aggregation, you're in family 2 or 3.

### Background — Why the codebase looks like this

The MCP server was originally a thin runtime CRUD proxy (Versions 1.0–1.1). Versions 1.2–1.3 added the full `octo-cli`
command surface via the SDK service clients, plus out-of-band file transfer. Version 1.4 added aggregation +
stream-data query parity with the asset-repo GraphQL transient-query API. Three families of tools coexist on purpose:

- **Family 1** talks HTTP to the backend services via `OctoServiceClientFactory` + `*ClientContext` helpers — same code
  path the CLI uses, so the orchestrated workflows (tenant create + admin provision, blueprint update, workload deploy
  through pool, etc.) work identically.
- **Family 2** talks directly to `ITenantRepository` (MongoDB) — fast generic CRUD and schema discovery, no
  platform-admin operations, no HTTP overhead.
- **Family 3** also talks directly to the engine (via `ITenantRepository` for runtime aggregations; via
  `ITenantContext.GetStreamDataRepository()` for stream-data queries), with its own lowercase enum + `AggregationMapper`
  conventions. Mirrors the asset-repo GraphQL transient-query surface so the AI never has to construct GraphQL.

Don't try to merge them. Generic CRUD doesn't go through the service clients (no HTTP overhead for read-heavy entity
queries); platform-admin operations don't bypass the service clients (skipping them would skip the orchestration);
aggregations don't go through `*ClientContext` (they need direct engine access for `RtEntityQueryOptions`
configuration). The three layers have different cost profiles and different validation needs.

## Read before you change

<!-- >>> generated: routing -->
| When you change | Read first |
|---|---|
| `**/Tools/{DataFlowTriggerDeploymentSite,TenantManagement,AiLifecycle,ApiSecret}Tools.cs`, `**/Services/{IdentityForbiddenMessage,AiClientContext}.cs` | `docs/admin-tools.md` |
| `**/Tools/{RuntimeAggregation,StreamData*}Tools.cs`, `**/Services/{AggregationMapper,AvailableArchivePaths*}.cs`, `**/Models/{Aggregation,Filters}/**` | `docs/aggregation-tools.md` |
| `**/Program.cs`, `**/Configuration/**`, `**/SystemApi/**`, `**/Services/McpSession*.cs`, `**/Services/Tenant*.cs`, `**/Tools/AuthenticationTools.cs` | `docs/authentication.md` |
| `src/McpServices.MethodClients/**`, `**/UserManagementTools.cs`, `**/RoleManagementTools.cs`, `**/ErrorTextRedactor.cs`, `**/McpPayloadLogFilter.cs` | `docs/ck-method-clients.md` |
| `**/Routing/**`, `**/Services/*FileTransferStore.cs`, `**/Services/{EndTrackingStream,CkDraftInlinePackager}.cs`, `**/Tools/{FileTransfer,PlatformFile,CkModel*}Tools.cs` | `docs/file-transfer.md` |
| `src/McpServices.ContentGuard/**`, `**/Services/ContentGuard/**`, `**/Services/KnowledgeUrl*.cs`, `**/KnowledgeFetchOptions.cs` | `docs/knowledge-input-guards.md` |
| `**/Tools/KnowledgeTools.cs`, `**/Services/Knowledge/**`, `**/Resources/Knowledge*.cs`, `**/Prompts/**`, `**/Configuration/McpServerSetup.cs` | `docs/knowledge-tools.md` |
| `**/Services/Knowledge{WriteRefusal,ImportCheck}.cs`, `**/Services/PrivateTempDirectory.cs`, `**/Knowledge*Update*.cs`, `**/Tools/KnowledgeLintTools.cs` | `docs/knowledge-write-guards.md` |
| `**/Tools/{RuntimeEntity,SchemaDiscovery}*.cs`, `**/Services/{Attribute,AccessAware,GenericCreate,QueryPaging,CkTypeId}*.cs`, `**/Architecture/**` | `docs/runtime-entity-tools.md` |
| `**/Services/{RuntimeSecurityContext,McpCallerPrincipal,AiServiceClientTrust,DataPermission}*.cs`, `**/CkSchemaResources.cs`, `**/EchoTool.cs` | `docs/tenant-gate.md` |
| `tests/McpServices.Tests/**`, `azure-pipelines.yml`, `run-tests.sh` | `docs/testing.md` |
| `src/McpServices/**`, `src/McpServices.Resources/**`, `tests/McpServices.Tests/Tools/**` | `docs/tool-conventions.md` |
<!-- <<< end generated: routing -->

Background, not path-triggered: `docs/project-layout.md` (folder map of the projects),
`docs/risk-classification-sweep.md` (the risk annotation of every tool, per family),
`docs/concepts/optimistic-locking-spike.md` (design spike behind `expected_version`) and
`docs/concepts/implementation-m3-phaseB-2c-2.md` (design sketch of `apply_custom_app_scaffold`).

## Build & test

```bash
# Build the MCP server
dotnet build src/McpServices/McpServices.csproj -c DebugL

# Build the entire solution (server + tests + resources)
dotnet build Octo.McpServices.sln -c DebugL

# Run all tests
dotnet test Octo.McpServices.sln -c DebugL

# Filter tests by class
dotnet test --filter "FullyQualifiedName~TenantManagementToolsTests"

# Run dev server (binds to 5017 by default — see launchSettings.json)
cd src/McpServices && dotnet run --environment Development
```

**Build configurations:** `Debug`, `Release`, `DebugL` (local dev with `OctoVersion=999.0.0`, uses local NuGet packages
from `../nuget/`).

**`TreatWarningsAsErrors` is enabled.** In particular, `CS1591` (missing XML doc) breaks the build for any public member
of `McpServices`. Every public type, property, and method on a new tool class needs an XML doc summary.

## Before you commit

1. `dotnet test Octo.McpServices.sln -c DebugL` — all green before commit.
2. Update `README.md` Available Tools section if you added a new category.
3. Update the doc under `docs/` that covers the code you changed (the routing table above names it), in the same
   commit.
4. If you touched `AGENTS.md` or any file in `docs/`: `Test-OctoAgentDocs -Fix` reports 0 errors.
   It rewrites the generated routing table, so run it before you stage, not after.

## Rules

- **This repository is public.** No work item ids, internal hostnames, customer or tenant names, or secrets in code
  comments, docs or test names.
- **Don't bypass `*ClientContext` helpers in a platform-admin tool (family 1).** Even if you only need one tenant for
  one call, go through them — they enforce auth + tenant resolution + factory routing uniformly. Family 2 and 3 do
  not use them; their rule is the direct-engine rule below.
- **Don't add a tool without tests.** The "I'll add tests later" pattern hasn't held up in this codebase; every commit
  landed with its tests in the same commit.
- **Don't accept base64-encoded file content as a tool parameter.** Use the file-transfer endpoints. They handle
  multi-GB files and stream from disk; base64 in JSON-RPC blows up token budgets and memory.
- **Don't downgrade `confirm: true` to a default-true.** AI clients should opt into destructive actions explicitly.
- **Don't write to `Console`** or rely on `ILogger` for user-visible output. Use the `Message` / `ErrorMessage` fields
  of the response envelope.
- **Don't share SDK clients across requests.** Per-tenant `ServiceUri` caching makes this unsafe.
- **Don't parse JWT tokens by hand.** The caller's identity comes from the validated request principal through
  `Services/McpCallerPrincipal.cs`; claims of an access token are read through `Services/JwtClaimReader.cs`. Add no
  further `JwtSecurityTokenHandler` call site (`IdentityTools.cs` and `AuthenticationTools.cs` still hold their own).
- **A direct-engine tool (family 2 or 3) acts as its caller.** Resolve the tenant and the security context with
  `RuntimeSecurityContextResolver` and open the session with that context; never the parameterless
  `GetSessionAsync()` and never `RtSecurityContext.System`: `docs/tenant-gate.md`.
- **A client or HTTP call routed to a tenant uses the tenant overload of the token resolution**
  (`ResolveAccessTokenAsync(server, tenantId)`); a failed exchange is an error, never a fallback to the home token:
  `docs/authentication.md`.
- **Both MCP endpoints keep the transport policy, and a new controller carries its own `[Authorize]`**; nothing in
  `Program.cs` gates a controller. Only `FileTransferController` is anonymous, by design (`docs/file-transfer.md`):
  `docs/authentication.md`.
- **Decide the `[McpRisk]` level of a new tool with its implementation.** A tool without the attribute counts as
  `Low` and runs without approval: `docs/tool-conventions.md`, section 8.
- **Knowledge pages are written only by the knowledge tools**, through the content guard; the generic write tools
  refuse those types: `docs/knowledge-write-guards.md`.
- **No token, secret or password in a response, an error message or a log line.** Pass exception text of a tool
  that handles them through `ErrorTextRedactor.Redact` and keep `McpPayloadLogFilter`; other tools do not redact
  yet: `docs/ck-method-clients.md`.
