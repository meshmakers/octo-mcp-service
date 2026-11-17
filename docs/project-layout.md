---
description: Annotated source tree of src/McpServices and the test project; a map of where the tool, service and model files live.
background: true
---

# Project layout

```
src/McpServices/
├── Program.cs                          # Composition root + endpoint mapping
├── appsettings.json                    # Includes OctoServiceUrls section
├── Options/
│   ├── McpServiceOptions.cs            # MCP-server-specific options (incl. RequiredApiScopes; AiServiceClientId)
│   ├── KnowledgeFetchOptions.cs        # Knowledge-source fetch policy
│   └── OctoServiceUrlOptions.cs        # Backend service URLs
├── Configuration/
│   └── McpAuthorizationPolicy.cs       # McpTransportPolicy scope requirement
├── Services/
│   ├── IOctoServiceClientFactory.cs    # SDK client factory interface
│   ├── OctoServiceClientFactory.cs     # Builds per-tenant SDK clients
│   ├── McpCallerPrincipal.cs           # The validated request principal (single claim reader)
│   ├── DataPermissionStreamGuard.cs    # Stream-data CK-type Read gate (mirrors asset-repo)
│   ├── AttributeAccessGuard.cs         # CK access rules for the generic tools
│   ├── AccessAwareRtEntityToDtoMapper.cs # The only entity mapper: strips Hidden attributes
│   ├── KnowledgeUrlPolicy.cs           # SSRF policy + IHostAddressResolver
│   ├── KnowledgeUrlFetcher.cs          # Guarded knowledge-source fetch
│   ├── McpSessionContext.cs            # Caller-bound store key + access token resolution
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
│   └── FileTransferController.cs       # PUT/GET /file-transfer/{upload,download}/{id}
│                                       # (the {tenantId} route constraint is shared — it lives in
│                                       #  octo-common-services and is registered via
│                                       #  AddOctoTenantIdRouteConstraint())
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
    ├── CrossTenantGrantPermissionTools.cs (source-tenant allow-list)
    ├── BlueprintTools.cs / CkModelLibraryTools.cs
    ├── CommunicationLifecycleTools.cs / AdapterTools.cs / PipelineTools.cs
    ├── DataFlowTriggerDeploymentSiteTools.cs / WorkloadTools.cs
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
