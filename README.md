# OctoMesh MCP Service

A comprehensive Model Context Protocol (MCP) server for OctoMesh Construction Kit operations, exposing **~174 tools** that mirror the full surface of `octo-cli`, the asset-repo GraphQL transient-query API, plus generic CK-type CRUD. AI assistants get direct access to tenant administration, identity management, communication-controller, blueprints, time-series queries + aggregations, reporting, and large-file transfers — without ever invoking the CLI or sending GraphQL.

## 🚀 Features

### **Platform Administration (~140 tools)**
- **Tenant lifecycle**: create, attach, detach, clean, clear cache, dump, restore
- **Identity** (Users, Roles, Groups, Clients, Identity Providers): full CRUD + cross-tenant mappings + admin provisioning
- **API access control**: API resources, API scopes, API secrets (client and resource variants)
- **OAuth client mirroring**: auto-provision flagged ClientCredentials clients into sub-tenants

### **Asset Repository (~28 tools)**
- **Blueprints**: install, history, preview/apply update, backups + rollback, uninstall with cascade
- **CK model libraries**: catalog browse, dependency resolution, fix-all
- **Runtime CK model + entity import/export** (file-based, via streaming upload/download endpoints)

### **Communication Controller (25 tools)**
- **Adapters + pipelines**: deploy, execute, debug capture, schema discovery
- **Data flows + triggers + pools**: deploy/undeploy + status
- **Workload CI/CD rollout** (Epic 3054): chart-version staging, deploy, bulk pipeline reassignment

### **Time Series, Reporting, Diagnostics (14 tools)**
- **Stream data + archives**: enable/disable, activate, freeze + rewind rollups
- **Reporting service**: enable/disable
- **Runtime log-level reconfiguration**: dispatches to all 6 backend services

### **File transfers (9 tools + 2 HTTP endpoints)**
- Multi-GB streaming uploads + range-enabled downloads, disk-backed buffers, 30 min TTL
- Tenant dump → downloadId, tenant restore via TUS-resumable upload
- CK model + runtime model import (with job polling) and export (with download URL)

### **Runtime + Stream Data Aggregations (8 tools)**
- **Runtime aggregations**: scalar + grouped (`avg(Power) group by Region`, `count(*)` …)
- **Stream-data queries**: raw rows, scalar aggregation, grouped aggregation, time-bucket downsampling
- **Archive metadata**: bulk storage stats (row count / on-disk size / health) + rollup query metadata
- Mirrors the asset-repo GraphQL transient-query surface so AI clients don't need GraphQL at all

### **Generic CK CRUD + Schema Discovery (15 tools)**
- **Universal entity management**: query/create/update/delete for any CK type
- **Schema discovery**: list types/models, get type schema, navigate associations
- **Tool monitoring**: usage statistics, parameter validation, health endpoints

## 🔧 Installation & Setup

### **Prerequisites**
- .NET 8.0 or later
- MongoDB instance
- OctoMesh Runtime Services

### **Configuration**

1. **Update appsettings.json**:
```json
{
  "DynamicTools": {
    "EnableDynamicToolGeneration": true,
    "MaxQueryResultLimit": 1000,
    "EnableToolStatistics": true,
    "CkTypeGraphCacheDurationMinutes": 30,
    "PreloadModels": ["System-1.0.0", "Basic-1.0.0"]
  },
  "Runtime": {
    "MongoDB": {
      "ConnectionString": "mongodb://localhost:27017",
      "DatabaseNamePrefix": "octo_"
    }
  },
  "OctoServiceUrls": {
    "AssetServiceUrl": "https://localhost:5001/",
    "IdentityServiceUrl": "https://localhost:5003/",
    "CommunicationServiceUrl": "https://localhost:5005/",
    "BotServiceUrl": "https://localhost:5007/",
    "ReportingServiceUrl": "https://localhost:5009/",
    "AdminPanelUrl": "https://localhost:5011/"
  }
}
```

`OctoServiceUrls` configures the backend service endpoints reached by the SDK-based platform-admin tools. Override per-environment via env vars: `OCTO_OCTOSERVICEURLS__ASSETSERVICEURL=https://asset.prod.example.com/` etc.

2. **Start the Service**:
```bash
cd src/McpServices
dotnet run
```

3. **Configure Claude Desktop**:
```json
{
  "mcpServers": {
    "octo-mesh": {
      "command": "node",
      "args": ["src/mcp-bridge.js"],
      "cwd": "/path/to/octo-mcp-service"
    }
  }
}
```

## 🛠️ Available Tools

> **174 tools total.** Most tools mirror the corresponding `octo-cli` command (snake_case naming); the aggregation tools mirror the asset-repo GraphQL transient-query surface. All platform-admin tools accept an optional `tenantId` parameter that falls back to the URL route. Destructive operations require an explicit `confirm: true` parameter (no silent state changes).

### **Authentication & Identity Bootstrap** (4)
`authenticate` · `check_auth_status` · `whoami` · `list_tenants`

### **Tenant Lifecycle** (9)
`get_tenants` · `create_tenant` · `delete_tenant`<sup>‡</sup> · `clean_tenant`<sup>‡</sup> · `attach_tenant` · `detach_tenant` · `clear_tenant_cache`<sup>‡</sup> · `update_system_ck_model` · `dump_tenant`<sup>📁</sup> · `restore_tenant`<sup>‡📁</sup>

### **Identity — Users / Roles / Groups** (21)
- Users (7): `get_users` · `create_user` · `update_user` · `delete_user`<sup>‡</sup> · `reset_user_password`<sup>‡</sup> · `add_user_to_role` · `remove_user_from_role`<sup>‡</sup>
- Roles (4): `get_roles` · `create_role` · `update_role` · `delete_role`<sup>‡</sup>
- Groups (10): `get_groups` · `get_group` · `create_group` · `update_group` · `delete_group`<sup>‡</sup> · `update_group_roles` · `add_user_to_group` · `remove_user_from_group`<sup>‡</sup> · `add_group_to_group` · `remove_group_from_group`<sup>‡</sup>

### **Identity — OAuth Clients + Identity Providers** (20)
- Clients (13): `get_clients` · `get_client` · `add_client_credentials_client` · `add_device_code_client` · `add_authorization_code_client` · `delete_client`<sup>‡</sup> · `add_scope_to_client` · `get_client_mirrors` · `provision_client_in_existing_tenants` · `provision_client_in_tenant` · `unprovision_client_from_tenant`<sup>‡</sup> · `set_client_auto_provision`
- Identity Providers (7): `get_identity_providers` · `delete_identity_provider`<sup>‡</sup> · `update_identity_provider` · `add_oauth_identity_provider` · `add_azure_entra_id_identity_provider` · `add_open_ldap_identity_provider` · `add_active_directory_identity_provider` · `add_octo_tenant_identity_provider`

### **Identity — API Resources / Scopes / Secrets** (16)
- API Resources (4): `get_api_resources` · `create_api_resource` · `update_api_resource` · `delete_api_resource`<sup>‡</sup>
- API Scopes (4): `get_api_scopes` · `create_api_scope` · `update_api_scope` · `delete_api_scope`<sup>‡</sup>
- API Secrets (8): `get_client_secrets` · `create_client_secret` · `update_client_secret` · `delete_client_secret`<sup>‡</sup> · `get_api_resource_secrets` · `create_api_resource_secret` · `update_api_resource_secret` · `delete_api_resource_secret`<sup>‡</sup>

### **Identity — Cross-Tenant Mappings + Admin Provisioning** (14)
- Email-Domain Group Rules (5): `get_email_domain_group_rules` · `get_email_domain_group_rule` · `create_email_domain_group_rule` · `update_email_domain_group_rule` · `delete_email_domain_group_rule`<sup>‡</sup>
- External-Tenant User Mappings (5): `get_external_tenant_user_mappings` · `get_external_tenant_user_mapping` · `create_external_tenant_user_mapping` · `update_external_tenant_user_mapping` · `delete_external_tenant_user_mapping`<sup>‡</sup>
- Admin Provisioning (4): `get_admin_provisioning_mappings` · `create_admin_provisioning_mapping` · `provision_current_user_as_admin` · `delete_admin_provisioning_mapping`<sup>‡</sup>

### **Asset Repository — Blueprints** (10)
`list_blueprints` · `install_blueprint` · `get_blueprint_history` · `get_blueprint_update_info` · `preview_blueprint_update` · `update_blueprint`<sup>‡</sup> · `list_blueprint_backups` · `rollback_blueprint`<sup>‡</sup> · `list_blueprint_installations` · `uninstall_blueprint`<sup>‡</sup>

### **Asset Repository — CK Model Libraries** (8)
`list_ck_catalogs` · `list_ck_catalog_models` · `refresh_ck_catalogs` · `get_ck_library_status` · `check_ck_dependencies` · `check_ck_upgrade` · `import_ck_from_catalog` · `fix_all_ck_models`<sup>‡</sup>

### **Communication Controller** (25)
- Lifecycle (2): `enable_communication` · `disable_communication`<sup>‡</sup>
- Adapters (4): `get_adapters` · `get_adapter` · `get_adapter_nodes` · `get_pipeline_schema`
- Pipelines (8): `get_pipeline_status` · `deploy_pipeline` · `execute_pipeline` · `set_pipeline_debug` · `get_pipeline_debug` · `get_pipeline_executions` · `get_latest_pipeline_execution` · `get_pipeline_debug_points`
- Data Flows / Triggers / Pools (6): `deploy_data_flow` · `undeploy_data_flow`<sup>‡</sup> · `get_data_flow_status` · `deploy_triggers` · `undeploy_triggers`<sup>‡</sup> · `get_pools`
- Workloads + CI/CD (5): `get_workloads_by_chart` · `update_workload_chart_version` · `deploy_workload` · `undeploy_workload`<sup>‡</sup> · `move_pipelines`<sup>‡</sup>

### **Time Series + Reporting + Diagnostics** (14)
- Stream Data + Archives (11): `enable_stream_data` · `disable_stream_data`<sup>‡</sup> · `activate_archive` · `disable_archive` · `enable_archive` · `retry_archive_activation` · `delete_archive`<sup>‡</sup> · `list_rollups_for_archive` · `freeze_rollup_archive` · `unfreeze_rollup_archive` · `rewind_rollup_watermark`<sup>‡</sup>
- Reporting (2): `enable_reporting` · `disable_reporting`<sup>‡</sup>
- Diagnostics (1): `reconfigure_log_level` (dispatches to Identity/AssetRepository/Communication/Reporting/Bot/AdminPanel)

### **File I/O** (9 tools + 2 HTTP endpoints)
- Foundation: `prepare_file_upload` · `cancel_file_transfer`
- CK Model Imports: `import_ck_model` · `import_runtime_model`
- CK Model Exports: `export_runtime_model_by_query` · `export_runtime_model_by_deep_graph`
- Tenant Backup: `dump_tenant` · `restore_tenant`<sup>‡</sup>
- Fixup Scripts: `run_fixup_scripts`<sup>‡</sup> (create via generic `create_entity` with `RtFixup` CK type)
- HTTP: `PUT /file-transfer/upload/{id}` · `GET /file-transfer/download/{id}` (range-enabled, 5 GiB cap)

### **Runtime + Stream Data Aggregations** (8)
- Runtime aggregation (2): `query_entities_aggregation` · `query_entities_grouping`
- Stream data (4): `query_stream_data_simple` · `query_stream_data_aggregation` · `query_stream_data_grouping` · `query_stream_data_downsampling`
- Archive metadata (2): `get_archive_storage_stats` · `get_rollup_query_metadata`

### **Generic Runtime CRUD + Schema Discovery** (15)
- CRUD (6): `query_entities` · `query_entities_simple` · `get_entity_by_id` · `create_entity` · `update_entity` · `delete_entity`<sup>‡</sup>
- Schema (5): `get_available_types` · `get_type_schema` · `get_available_models` · `search_types` · `get_association_tree` · `navigate_associations`
- Tool Management (4): `list_available_tools` · `get_tool_details` · `get_tool_statistics` · `validate_tool_parameters`
- Echo (1): `Echo`

<sup>‡</sup> Destructive — requires `confirm: true`.
<sup>📁</sup> Uses file-transfer endpoints (see Section *File I/O Flow* below).

## 📂 File I/O Flow

Large-file operations use out-of-band streaming endpoints alongside the JSON-RPC channel.

### Upload flow (import / restore)
```
1. Tool call: prepare_file_upload(fileName)
   → { transferId, uploadUrlPath: "/file-transfer/upload/{transferId}" }
2. HTTP PUT the file body to <publicUrl> + uploadUrlPath
   → { transferId, sizeBytes }
3. Tool call: import_ck_model(transferId, tenantId)
   or:        restore_tenant(transferId, targetTenantId, databaseName, confirm: true)
   → waits for the asset / bot job to finish (default timeout 10 / 30 min)
```

### Download flow (export / dump)
```
1. Tool call: dump_tenant(targetTenantId)
   or:        export_runtime_model_by_query(queryId)
   → { transferId, downloadUrlPath: "/file-transfer/download/{transferId}" }
2. HTTP GET <publicUrl> + downloadUrlPath
   → Streams the bytes with Content-Disposition + range support
```

Reservations and downloads expire after **30 minutes**; a background sweeper purges them.

## 📖 Usage Examples

### **List child tenants**
```json
{
  "tool": "get_tenants",
  "parameters": { "tenantId": "octosystem" }
}
```

### **Create a sub-tenant + provision the calling user as admin**
```json
{ "tool": "create_tenant", "parameters": { "childTenantId": "acme", "database": "acme_db" } }
{ "tool": "provision_current_user_as_admin", "parameters": { "targetTenantId": "acme" } }
```

### **Roll out a new chart version across all matching workloads (CI/CD)**
```json
{ "tool": "get_workloads_by_chart", "parameters": { "chartName": "octo-mesh-adapter" } }
{ "tool": "update_workload_chart_version", "parameters": { "workloadId": "wl-123", "chartVersion": "1.2.4" } }
{ "tool": "deploy_workload", "parameters": { "workloadId": "wl-123" } }
```

### **Install a blueprint and inspect the resulting installation**
```json
{ "tool": "install_blueprint", "parameters": { "blueprintId": "EnergyCommunity-1.0.0" } }
{ "tool": "list_blueprint_installations" }
```

### **Generic CK entity query**
```json
{
  "tool": "query_entities",
  "parameters": {
    "ckTypeId": "EnergyCommunity-1.0.0/Customer-1.0.0",
    "filters": { "Operator": "And", "Fields": [{ "Path": "State", "Operator": "Equals", "Value": "Active" }] },
    "limit": 50
  }
}
```

### **Dump a tenant and download the .tar.gz**
```json
{ "tool": "dump_tenant", "parameters": { "targetTenantId": "acme" } }
// Response: { transferId: "abc123", downloadUrlPath: "/file-transfer/download/abc123", ... }
// Then: HTTP GET https://mcp.example.com/file-transfer/download/abc123
```

### **Aggregate sensor readings grouped by facility**
```json
{
  "tool": "query_entities_grouping",
  "parameters": {
    "ckTypeId": "Industry.Energy-1/Sensor-1",
    "groupByAttributePaths": ["FacilityId", "Region"],
    "aggregations": [
      { "function": "count" },
      { "function": "avg", "attributePath": "Power", "alias": "avgPower" },
      { "function": "max", "attributePath": "Power", "alias": "peakPower" }
    ]
  }
}
// Response: { rows: [{ FacilityId: "F1", Region: "EU", count: 12, avgPower: 5.2, peakPower: 11 }, …] }
```

### **Downsample a sensor archive into hourly buckets**
```json
{
  "tool": "query_stream_data_downsampling",
  "parameters": {
    "archiveRtId": "69fda707d47638c68edc7fea",
    "aggregations": [
      { "function": "avg", "attributePath": "Power" },
      { "function": "max", "attributePath": "Power", "alias": "peak" }
    ],
    "from": "2026-06-01T00:00:00Z",
    "to":   "2026-06-08T00:00:00Z",
    "limit": 168
  }
}
// Response: rows = one bucket per hour, columns: bucketStart, avg_Power, peak
```

## 🏗️ Architecture

### **Service Architecture**
```
┌─────────────────────┐
│   Claude Desktop    │
├─────────────────────┤
│   MCP Bridge        │
├─────────────────────┤
│   MCP Server        │
│   - Tool Discovery  │
│   - Execution       │
│   - Validation      │
├─────────────────────┤
│   Domain Services   │
│   - Dynamic CRUD    │
│   - Analytics       │
│   - Monitoring      │
├─────────────────────┤
│   OctoMesh Runtime  │
│   - CK Engine       │
│   - MongoDB Repo    │
└─────────────────────┘
```

### **Tool Categories**
- **Platform Admin Tools**: thin wrappers over the SDK service clients (Identity, Asset, Communication, Reporting, StreamData, Bot, AdminPanel) — equivalent surface to `octo-cli`
- **Generic CRUD Tools**: universal entity operations for any CK type, talking directly to the runtime engine
- **Schema Tools**: discovery and exploration of data models
- **File Transfer Tools**: out-of-band streaming endpoints for import/export/dump/restore
- **Management Tools**: tool monitoring and administration (`get_tool_statistics`, `validate_tool_parameters`, etc.)

### **Caching Strategy**
- **CK Type Graphs**: Cached for 30 minutes (configurable)
- **Available Types**: Per-tenant caching
- **Tool Statistics**: In-memory aggregation
- **Performance Metrics**: Real-time collection

## 🔧 Configuration Options

### **Dynamic Tool Options**
| Setting | Default | Description |
|---------|---------|-------------|
| `EnableDynamicToolGeneration` | `true` | Enable automatic tool generation |
| `MaxQueryResultLimit` | `1000` | Maximum entities per query |
| `DefaultQueryLimit` | `100` | Default result limit |
| `AnalyticsTimeoutSeconds` | `300` | Timeout for long operations |
| `EnableToolStatistics` | `true` | Collect usage statistics |
| `CkTypeGraphCacheDurationMinutes` | `30` | Cache duration for schemas |

### **OctoServiceUrls (Backend Endpoints)**
| Setting | Used By |
|---------|---------|
| `AssetServiceUrl` | Tenant lifecycle, Blueprints, CK Model Libraries, Models, Stream Data tools |
| `IdentityServiceUrl` | All Identity tools (Users, Roles, Groups, Clients, Providers, API resources/scopes/secrets) |
| `CommunicationServiceUrl` | Communication Controller tools (adapters, pipelines, workloads, data flows, triggers, pools) |
| `BotServiceUrl` | File-IO downloads, fixup scripts, tenant dump/restore, log-level dispatch |
| `ReportingServiceUrl` | Reporting service tools |
| `AdminPanelUrl` | Admin Panel log-level dispatch only |

Each URL may be empty if you don't use the matching tools — the factory throws `ServiceConfigurationMissingException` on the first call into an unconfigured client.

## 📊 Monitoring & Health Checks

### **Health Endpoints**
- `/health` - Overall service health
- `/health/ready` - Readiness probe
- `/health/live` - Liveness probe

### **Tool Statistics**
Access real-time tool usage statistics:
```json
{
  "tool": "get_tool_statistics",
  "parameters": {
    "timeRange": "day"
  }
}
```

### **Performance Metrics**
- Execution times per tool
- Success/failure rates
- Cache hit ratios
- Error categorization

## 🔐 Authentication & Multi-Tenant Access

### **OAuth2 Device Authorization Flow**

The MCP server supports authentication via the OAuth2 Device Authorization Flow, ideal for CLI and AI clients (e.g., Claude Code). No browser redirect required on the client side.

**Flow:**
1. Client calls `authenticate` tool
2. Server returns a user code and verification URL
3. User opens the URL in a browser, enters the code, and logs in
4. Client calls `check_auth_status` to complete authentication
5. Server stores tokens per MCP session (automatic refresh)

**Identity Server Client:** `octo-mcpServices-device` (registered automatically on startup)

### **Tenant Resolution (Stateless)**

Tenants are resolved per-request using this priority:
1. **Tool parameter `tenantId`** (explicit, from any endpoint)
2. **Route parameter `{tenantId}`** (from `/{tenantId}/mcp` endpoint)
3. Error if no tenant can be resolved

This is fully stateless — no tenant is stored in session state. The AI client remembers the tenant in its conversation context.

### **Endpoints**

| Endpoint | Description |
|----------|-------------|
| `/{tenantId}/mcp` | Tenant-scoped MCP endpoint (backwards compatible) |
| `/mcp` | Tenantless MCP endpoint (tenant via tool parameter) |

### **Claude Code Configuration (single entry)**

```json
{
  "mcpServers": {
    "octomesh": {
      "type": "http",
      "url": "https://mcp.example.com/mcp"
    }
  }
}
```

### **Authentication & Identity Tools**

| Tool | Auth Required | Description |
|------|---------------|-------------|
| `authenticate` | No | Start Device Authorization Flow |
| `check_auth_status` | No | Check if user completed browser authentication |
| `whoami` | Yes | User info (name, email, roles, tenants) |
| `list_tenants` | Yes | List all tenants the user has access to |

## 🔒 Security & Permissions

### **Tenant Isolation**
- All operations are tenant-scoped
- Tenant resolution from tool parameter or URL path
- Isolated data access per tenant
- `allowed_tenants` JWT claim validation

### **Parameter Validation**
- Type-safe parameter validation
- Schema-based entity validation
- SQL injection prevention
- Input sanitization

### **Error Handling**
- Detailed errors in development
- Sanitized errors in production
- Structured error responses
- Error correlation IDs

## 🚀 Development

### **Adding New Tools**

Tools follow a strict pattern — see `CLAUDE.md` for the full conventions. The short version:

1. Pick the right SDK Client Context (`IdentityClientContext`, `AssetClientContext`, `CommunicationClientContext`, `StreamDataClientContext`, `ReportingClientContext`, or `BotClientContext`) — these handle auth + tenant resolution + factory call.
2. Use the standard response envelope (`*Response` with `IsSuccess`/`ErrorMessage`/`Message`/`TenantId`).
3. Destructive operations MUST require an explicit `confirm: true` bool parameter — no interactive prompts.
4. Add at least four tests per tool: happy path, unauthenticated, missing required args, destructive without confirm (where applicable).

```csharp
[McpServerToolType]
public sealed class MyTools
{
    [McpServerTool(Name = "my_tool")]
    [Description("Equivalent to octo-cli MyCommand.")]
    public static async Task<MyResponse> MyTool(
        McpServer server,
        [Description("Some required arg.")] string requiredArg,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(requiredArg))
        {
            return new MyResponse { IsSuccess = false, ErrorMessage = "requiredArg is required." };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new MyResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DoSomething(requiredArg);
            return new MyResponse { IsSuccess = true, TenantId = ctx.TenantId, Message = "..." };
        }
        catch (Exception ex)
        {
            return new MyResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
```

### **Testing**
```bash
# Run all tests (400 tests, ~250 ms)
dotnet test Octo.McpServices.sln -c DebugL

# Run tests for a specific tool class
dotnet test --filter "FullyQualifiedName~TenantManagementToolsTests"

# Build the MCP server
dotnet build src/McpServices/McpServices.csproj -c DebugL

# Start dev server
cd src/McpServices && dotnet run --environment Development
```

## 📝 Changelog

### **Version 1.4.0** — Runtime + Stream Data Aggregations
- Eight new tools mirror the asset-repo GraphQL transient-query surface:
  - Runtime: `query_entities_aggregation`, `query_entities_grouping`
  - Stream data: `query_stream_data_simple`, `query_stream_data_aggregation`, `query_stream_data_grouping`, `query_stream_data_downsampling`
  - Bonus reads: `get_archive_storage_stats`, `get_rollup_query_metadata`
- Lowercase aggregation function strings (`sum`/`avg`/`min`/`max`/`count`) — AI-ergonomic, mirrors SQL conventions
- Default response-column aliases (`avg_Power`, `count`, …) with optional explicit override per column
- Pre-SDK validation: at-least-one rule, attribute-path required for non-count, alias uniqueness, group-by duplicate check
- `ITenantResolutionService.GetTenantContextAsync` added — unlocks `ITenantContext.GetStreamDataRepository`/`GetArchiveRuntimeStore`/`GetRollupArchiveRuntimeStore`
- `StreamDataContext` helper encapsulates the four-stage resolution cascade (Tenant → Context → StreamRepo → Archive snapshot)
- 51 new tests; suite now passes 451/451

### **Version 1.3.0** — File I/O
- `prepare_file_upload` + `cancel_file_transfer` plus `PUT /file-transfer/upload/{id}` and `GET /file-transfer/download/{id}` HTTP endpoints (disk-backed, 5 GiB cap, 30 min TTL)
- `import_ck_model`, `import_runtime_model`, `export_runtime_model_by_query`, `export_runtime_model_by_deep_graph` — synchronous job polling, returns downloadId for exports
- `dump_tenant` + `restore_tenant` — multi-GB streaming, TUS resumable upload for restore
- `run_fixup_scripts` (Bot service)
- `BotClientContext` + `JobPollingHelper` infrastructure

### **Version 1.2.0** — Full octo-cli command coverage
- Phase 1: tenant lifecycle + identity (users / roles / groups / clients / providers) — 48 tools
- Phase 2: asset (blueprints + CK model libraries) — 18 tools
- Phase 3: communication controller (adapters / pipelines / workloads / data flows / triggers / pools) — 25 tools
- Phase 4: time series + reporting + diagnostics — 14 tools
- Phase 5: identity long-tail (API resources / scopes / secrets, email-domain rules, external tenant mappings, admin provisioning, polymorphic `update_identity_provider`) — 32 tools
- `OctoServiceClientFactory` builds per-tenant SDK clients; each request gets a fresh client with the session's access token
- Destructive operations require explicit `confirm: true` (no interactive prompts)
- 364 unit tests covering all new tools

### **Version 1.1.0**
- OAuth2 Device Authorization Flow for CLI/AI client authentication
- Tenantless `/mcp` endpoint with per-tool `tenantId` parameter
- `authenticate`, `check_auth_status`, `whoami`, `list_tenants` tools
- Identity Server client registration (`octo-mcpServices-device`, `octo-mcpServices-swagger`)
- Session-based token management with automatic refresh
- `ITenantResolutionService` for stateless multi-tenant tool access
- All existing tools extended with optional `tenantId` parameter

### **Version 1.0.0**
- ✅ Dynamic CRUD operations for all CK types
- ✅ Schema discovery and exploration tools
- ✅ Energy community analytics
- ✅ Industrial IoT monitoring
- ✅ Maintenance management tools
- ✅ Advanced analytics and reporting
- ✅ Tool management and statistics
- ✅ Health monitoring and validation
- ✅ Comprehensive configuration options
- ✅ Multi-tenant support

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch
3. Implement changes with tests
4. Update documentation
5. Submit a pull request

## 📄 License

This project is licensed under the MIT License - see the LICENSE file for details.

## 🆘 Support

For support and questions:
- Check the tool documentation: `get_tool_details`
- Monitor tool statistics: `get_tool_statistics`
- Review health status: `/health`
- Contact the development team

---

**Built with ❤️ for the OctoMesh ecosystem**
