# AGENTS.md

Instructions for coding agents in this repository. Detail lives in `docs/`; read the matching file before changing the code it covers.

## What this repo is

`octo-mcp-service` is the **Model Context Protocol** server for OctoMesh. It exposes 220 tools that mirror the full `octo-cli` command surface plus generic CK-type CRUD, so AI assistants can administer the platform end-to-end without invoking the CLI.

Three distinct tool families live here — be aware which one you're touching:

1. **Platform-admin tools** — thin wrappers over the `Meshmakers.Octo.Sdk.ServiceClient` SDK. One tool per `octo-cli` command. These tools talk HTTP to the Identity / Asset / Communication / Reporting / StreamData / Bot services.
2. **Generic CK CRUD + schema tools** — predate the platform-admin tools and talk directly to the runtime engine (MongoDB) via `ITenantRepository`. These do not use the SDK service clients.
3. **Aggregation + stream-data query tools** — newer; mirror the asset-repo GraphQL transient-query surface. They share family 2's path (talk to the engine directly via `ITenantRepository` / `ITenantContext.GetStreamDataRepository`), but use the lowercase `AggregationFunctionDto` enum and the `AggregationMapper` helper — *not* the platform-admin `*ClientContext` pattern.

If you're adding a tool that mirrors an `octo-cli` command, you're in family 1 — follow the `*ClientContext` pattern below. If you're adding a runtime/stream-data read or aggregation, you're in family 2 or 3.

### Background — Why the codebase looks like this

The MCP server was originally a thin runtime CRUD proxy (Versions 1.0–1.1). Versions 1.2–1.3 added the full `octo-cli` command surface via the SDK service clients, plus out-of-band file transfer. Version 1.4 added aggregation + stream-data query parity with the asset-repo GraphQL transient-query API. Three families of tools coexist on purpose:

- **Family 1** talks HTTP to the backend services via `OctoServiceClientFactory` + `*ClientContext` helpers — same code path the CLI uses, so the orchestrated workflows (tenant create + admin provision, blueprint update, workload deploy through pool, etc.) work identically.
- **Family 2** talks directly to `ITenantRepository` (MongoDB) — fast generic CRUD and schema discovery, no platform-admin operations, no HTTP overhead.
- **Family 3** also talks directly to the engine (via `ITenantRepository` for runtime aggregations; via `ITenantContext.GetStreamDataRepository()` for stream-data queries), with its own lowercase enum + `AggregationMapper` conventions. Mirrors the asset-repo GraphQL transient-query surface so the AI never has to construct GraphQL.

Don't try to merge them. Generic CRUD doesn't go through the service clients (no HTTP overhead for read-heavy entity queries); platform-admin operations don't bypass the service clients (skipping them would skip the orchestration); aggregations don't go through `*ClientContext` (they need direct engine access for `RtEntityQueryOptions` configuration). The three layers have different cost profiles and different validation needs.

## Read before you change

<!-- >>> generated: routing -->
<!-- <<< end generated: routing -->

## Build & test

```bash
# Build the MCP server
dotnet build src/McpServices/McpServices.csproj -c DebugL

# Build the entire solution (server + tests + resources)
dotnet build Octo.McpServices.sln -c DebugL

# Run all tests (currently 1893, ~2 s)
dotnet test Octo.McpServices.sln -c DebugL

# Filter tests by class
dotnet test --filter "FullyQualifiedName~TenantManagementToolsTests"

# Run dev server (binds to 5017 by default — see launchSettings.json)
cd src/McpServices && dotnet run --environment Development
```

**Build configurations:** `Debug`, `Release`, `DebugL` (local dev with `OctoVersion=999.0.0`, uses local NuGet packages from `../nuget/`).

**`TreatWarningsAsErrors` is enabled.** In particular, `CS1591` (missing XML doc) breaks the build for any public member of `McpServices`. Every public type, property, and method on a new tool class needs an XML doc summary.

## Before you commit

1. `dotnet test Octo.McpServices.sln -c DebugL` — all green before commit.
2. Update `README.md` Available Tools section if you added a new category.
3. If you touched `AGENTS.md` or any file in `docs/`: `Test-OctoAgentDocs -Fix` reports 0 errors.
   It rewrites the generated routing table, so run it before you stage, not after.

## Rules

- **Don't bypass `*ClientContext` helpers.** Even if you only need one tenant for one call, go through them — they enforce auth + tenant resolution + factory routing uniformly.
- **Don't add a tool without tests.** The "I'll add tests later" pattern hasn't held up in this codebase; every commit landed with its tests in the same commit.
- **Don't accept base64-encoded file content as a tool parameter.** Use the file-transfer endpoints. They handle multi-GB files and stream from disk; base64 in JSON-RPC blows up token budgets and memory.
- **Don't downgrade `confirm: true` to a default-true.** AI clients should opt into destructive actions explicitly.
- **Don't write to `Console`** or rely on `ILogger` for user-visible output. Use the `Message` / `ErrorMessage` fields of the response envelope.
- **Don't share SDK clients across requests.** Per-tenant `ServiceUri` caching makes this unsafe.
- **Don't manually parse JWT tokens** outside `IdentityTools.cs`. Use the existing pattern (`JwtSecurityTokenHandler`) — or better, lift it into a helper if a third call site appears.
