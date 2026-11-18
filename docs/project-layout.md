---
description: Folder map of the four projects and the test project; says what each folder holds and which doc covers it. No per-file list.
background: true
---

# Project layout

A map at folder level. Files are named only where they are an entry point; list a folder to see the rest. The doc
that explains a folder is named behind it (routing table in `AGENTS.md`).

```
Octo.McpServices.sln
src/
├── McpServices/                        # The MCP server (ASP.NET host, all tools)
│   ├── Program.cs                      # Composition root, middleware order, endpoint mapping  -> authentication.md
│   ├── Constants.cs                    # Shared constants (error texts)
│   ├── appsettings*.json               # Options sections (Mcp, DynamicTools, OctoServiceUrls)
│   ├── Configuration/                  # Options configurators, McpAuthorizationPolicy, the AI service client
│   │                                   # guard, McpPayloadLogFilter, McpServerSetup         -> authentication.md
│   ├── Options/                        # McpServiceOptions, OctoServiceUrlOptions, DynamicToolOptions,
│   │                                   # KnowledgeFetchOptions
│   ├── Tools/                          # One class per tool group, every [McpServerTool]; plus ErrorTextRedactor
│   │                                   #                                                -> tool-conventions.md
│   ├── Models/                         # Response envelopes and request DTOs per tool group; McpRiskAttribute,
│   │                                   # McpRiskLevel                                   -> tool-conventions.md
│   │   ├── Aggregation/                # Lowercase function enum, columns, result shapes     -> aggregation-tools.md
│   │   └── Filters/                    # Field filter DTOs of the query tools                -> aggregation-tools.md
│   ├── Services/                       # Everything the tools share:
│   │   │                               #   *ClientContext + OctoServiceClientFactory (family 1)
│   │   │                               #   McpSession*, Tenant*, McpCallerPrincipal, JwtClaimReader,
│   │   │                               #   RuntimeSecurityContextResolver, AiServiceClientTrust,
│   │   │                               #   DataPermissionStreamGuard        -> authentication.md, tenant-gate.md
│   │   │                               #   AttributeAccessGuard, AccessAwareRtEntityToDtoMapper, CkTypeIdResolver,
│   │   │                               #   QueryPaging, GenericCreateRefusal           -> runtime-entity-tools.md
│   │   │                               #   AggregationMapper, AvailableArchivePathsResolver -> aggregation-tools.md
│   │   │                               #   FileTransferStore, JobPollingHelper              -> file-transfer.md
│   │   │                               #   KnowledgeUrl*, KnowledgeWriteRefusal, KnowledgeImportCheck
│   │   │                               #                       -> knowledge-input-guards.md, knowledge-write-guards.md
│   │   │                               #   ToolRiskRegistry, DynamicToolService, ToolExecutionService, ToolUsageStats
│   │   ├── Knowledge/                  # Pages, conventions, search stemmer, platform updates -> knowledge-tools.md
│   │   ├── ContentGuard/               # Tenant secret fingerprints                    -> knowledge-input-guards.md
│   │   └── WorkloadResources/          # Kubernetes quantities and resource settings of the workload tools
│   ├── Resources/                      # MCP resources: CK schema, knowledge sources and pages
│   ├── Prompts/                        # MCP prompts (knowledge first)                       -> knowledge-tools.md
│   ├── Routing/                        # FileTransferController (/file-transfer/...)         -> file-transfer.md
│   ├── SystemApi/v1/Controllers/       # DiagnosticsController (REST, not the MCP transport) -> authentication.md
│   ├── Consumers/                      # Distributed event consumer for tenant updates
│   └── Utils/                          # XML documentation provider for tool descriptions
├── McpServices.ContentGuard/           # Content guard library: detectors, guard, models -> knowledge-input-guards.md
├── McpServices.MethodClients/          # Generated CK method clients (construction-kits.yaml) -> ck-method-clients.md
└── McpServices.Resources/              # Localised texts (McpTexts.resx)
tests/McpServices.Tests/                # xUnit test project                                   -> testing.md
├── TestBase.cs, ToolTestBase.cs        # Base classes: mocks, caller principal, SDK client mocks
├── TestJwt.cs, TestServiceProvider.cs, CkGraphTestBuilder.cs
├── McpServerIntegrationTests.cs
├── Tools/                              # One test file (or more) per Tools/ class; Fixtures/ holds test data
├── Services/                           # Service, context, store and guard tests (Knowledge/ below it)
├── Configuration/                      # Options, policy and authorization pipeline tests
├── Architecture/                       # Source-scanning rules (hidden attributes, secret decryption)
├── Resources/, Prompts/, Routing/      # Tests of the same-named server folders
```

The `{tenantId}` route constraint is shared: it lives in `octo-common-services` and is registered with
`AddOctoTenantIdRouteConstraint()`.
