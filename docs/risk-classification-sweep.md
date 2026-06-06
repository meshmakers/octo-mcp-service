# MCP Tool Risk-Classification Sweep — Domain-Owner Checklist (#4129)

**Source-of-truth sweep**: `88a2072` (#4111 Phase A) shipped the infrastructure;
this sweep annotated 101 tools across 25 files based on a pattern-deterministic
classifier. Run `dotnet test -c DebugL` — 510/510 pass with the annotations on.

**Scope of this doc**: every tool, every family, its current annotation. Owner
review per family below; flip any annotation that doesn't match the family's
operational reality. Edit the source file + the table in the same PR.

## Summary

- Total tools: 177
- Low (default, no `[McpRisk]` attribute): 74
- Low (explicit `[McpRisk(McpRiskLevel.Low)]`): 1
- Medium: 49
- High: 53

## Classification convention

- **Low** — read-only, single-instance writes with narrow blast radius. Worker runs without prompting.
- **Medium** — single-instance deletes, schema introspection, audit-worthy writes. Worker logs + UI status, no approval pause.
- **High** — bulk delete, schema/CK destruction, production deploy, force-push. Worker pauses + approval flow.

See `Models/McpRiskLevel.cs` for the enum docstrings.

## Per-family review

### AdapterTools (4 tools)

_Low=4_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_adapters` | **L** | |
| `get_adapter` | **L** | |
| `get_adapter_nodes` | **L** | |
| `get_pipeline_schema` | **L** | |

### AdminProvisioningTools (4 tools)

_High=2 · Low=1 · Medium=1_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_admin_provisioning_mappings` | **L** | |
| `create_admin_provisioning_mapping` | **H** | |
| `provision_current_user_as_admin` | **H** | |
| `delete_admin_provisioning_mapping` | **M** | |

### ApiResourceTools (4 tools)

_High=3 · Low=1_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_api_resources` | **L** | |
| `create_api_resource` | **H** | |
| `update_api_resource` | **H** | |
| `delete_api_resource` | **H** | |

### ApiScopeTools (4 tools)

_High=3 · Low=1_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_api_scopes` | **L** | |
| `create_api_scope` | **H** | |
| `update_api_scope` | **H** | |
| `delete_api_scope` | **H** | |

### ApiSecretTools (8 tools)

_High=6 · Low=2_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_client_secrets` | **L** | |
| `create_client_secret` | **H** | |
| `update_client_secret` | **H** | |
| `delete_client_secret` | **H** | |
| `get_api_resource_secrets` | **L** | |
| `create_api_resource_secret` | **H** | |
| `update_api_resource_secret` | **H** | |
| `delete_api_resource_secret` | **H** | |

### AuthenticationTools (2 tools)

_Low=2_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `authenticate` | **L** | |
| `check_auth_status` | **L** — owner: confirm | |

### BlueprintTools (10 tools)

_High=3 · Low=6 · Medium=1_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `list_blueprints` | **L** | |
| `install_blueprint` | **H** | |
| `get_blueprint_history` | **L** | |
| `get_blueprint_update_info` | **L** | |
| `preview_blueprint_update` | **L** | |
| `update_blueprint` | **M** | |
| `list_blueprint_backups` | **L** | |
| `rollback_blueprint` | **H** | |
| `list_blueprint_installations` | **L** | |
| `uninstall_blueprint` | **H** | |

### CkModelFileTools (4 tools)

_Low=2 · Medium=2_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `import_ck_model` | **M** | |
| `import_runtime_model` | **M** | |
| `export_runtime_model_by_query` | **L** | |
| `export_runtime_model_by_deep_graph` | **L** | |

### CkModelLibraryTools (8 tools)

_High=1 · Low=5 · Medium=2_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `list_ck_catalogs` | **L** | |
| `list_ck_catalog_models` | **L** | |
| `refresh_ck_catalogs` | **M** — owner: confirm | |
| `get_ck_library_status` | **L** | |
| `check_ck_dependencies` | **L** — owner: confirm | |
| `check_ck_upgrade` | **L** — owner: confirm | |
| `import_ck_from_catalog` | **M** | |
| `fix_all_ck_models` | **H** — owner: confirm | |

### ClientManagementTools (12 tools)

_High=4 · Low=3 · Medium=5_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_clients` | **L** | |
| `get_client` | **L** | |
| `add_client_credentials_client` | **M** | |
| `add_device_code_client` | **M** | |
| `add_authorization_code_client` | **M** | |
| `delete_client` | **H** | |
| `get_client_mirrors` | **L** | |
| `provision_client_in_existing_tenants` | **H** | |
| `provision_client_in_tenant` | **H** | |
| `unprovision_client_from_tenant` | **H** | |
| `set_client_auto_provision` | **M** | |
| `add_scope_to_client` | **M** | |

### CommunicationLifecycleTools (2 tools)

_High=2_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `enable_communication` | **H** | |
| `disable_communication` | **H** | |

### DataFlowTriggerPoolTools (6 tools)

_High=4 · Low=2_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `deploy_data_flow` | **H** | |
| `undeploy_data_flow` | **H** | |
| `get_data_flow_status` | **L** | |
| `deploy_triggers` | **H** | |
| `undeploy_triggers` | **H** | |
| `get_pools` | **L** | |

### DiagnosticsTools (1 tool)

_Medium=1_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `reconfigure_log_level` | **M** | |

### EmailDomainGroupRuleTools (5 tools)

_Low=2 · Medium=3_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_email_domain_group_rules` | **L** | |
| `get_email_domain_group_rule` | **L** | |
| `create_email_domain_group_rule` | **M** | |
| `update_email_domain_group_rule` | **M** | |
| `delete_email_domain_group_rule` | **M** | |

### ExternalTenantUserMappingTools (5 tools)

_Low=2 · Medium=3_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_external_tenant_user_mappings` | **L** | |
| `get_external_tenant_user_mapping` | **L** | |
| `create_external_tenant_user_mapping` | **M** | |
| `update_external_tenant_user_mapping` | **M** | |
| `delete_external_tenant_user_mapping` | **M** | |

### FileTransferTools (3 tools)

_High=1 · Low=2_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `prepare_file_upload` | **L** | |
| `cancel_file_transfer` | **L** | |
| `run_fixup_scripts` | **H** | |

### GroupManagementTools (10 tools)

_Low=2 · Medium=8_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_groups` | **L** | |
| `get_group` | **L** | |
| `create_group` | **M** | |
| `update_group` | **M** | |
| `delete_group` | **M** | |
| `update_group_roles` | **M** | |
| `add_user_to_group` | **M** | |
| `remove_user_from_group` | **M** | |
| `add_group_to_group` | **M** | |
| `remove_group_from_group` | **M** | |

### IdentityProviderTools (8 tools)

_High=2 · Low=1 · Medium=5_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_identity_providers` | **L** | |
| `delete_identity_provider` | **H** | |
| `add_oauth_identity_provider` | **M** | |
| `add_azure_entra_id_identity_provider` | **M** | |
| `add_open_ldap_identity_provider` | **M** | |
| `add_active_directory_identity_provider` | **M** | |
| `add_octo_tenant_identity_provider` | **M** | |
| `update_identity_provider` | **H** | |

### IdentityTools (2 tools)

_Low=2_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `whoami` | **L** — owner: confirm | |
| `list_tenants` | **L** | |

### PipelineTools (8 tools)

_High=2 · Low=5 · Medium=1_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_pipeline_status` | **L** | |
| `deploy_pipeline` | **H** | |
| `execute_pipeline` | **H** — owner: confirm | |
| `set_pipeline_debug` | **M** | |
| `get_pipeline_debug` | **L** | |
| `get_pipeline_executions` | **L** | |
| `get_latest_pipeline_execution` | **L** | |
| `get_pipeline_debug_points` | **L** | |

### ReportingTools (2 tools)

_High=2_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `enable_reporting` | **H** | |
| `disable_reporting` | **H** | |

### RiskMetadataTools (1 tool)

_Low=1_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_tool_risk_metadata` | **L** | |

### RoleManagementTools (4 tools)

_Low=1 · Medium=3_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_roles` | **L** | |
| `create_role` | **M** | |
| `update_role` | **M** | |
| `delete_role` | **M** | |

### RuntimeAggregationTools (3 tools)

_Low=3_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `execute_runtime_query` | **L** — owner: confirm | |
| `query_entities_aggregation` | **L** | |
| `query_entities_grouping` | **L** | |

### RuntimeEntityCrudTools (8 tools)

_Low=5 · Medium=3_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `query_entities` | **L** | |
| `query_entities_simple` | **L** | |
| `get_entity_by_id` | **L** | |
| `create_entity` | **M** | |
| `update_entity` | **M** | |
| `delete_entity` | **M** | |
| `navigate_associations` | **L** — owner: confirm | |
| `get_association_tree` | **L** | |

### SchemaDiscoveryTools (5 tools)

_Low=5_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_available_archive_paths` | **L** | |
| `get_available_models` | **L** | |
| `get_available_types` | **L** | |
| `get_type_schema` | **L** | |
| `search_types` | **L** | |

### StreamDataAggregationTools (5 tools)

_Low=5_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `execute_stream_data_query` | **L** — owner: confirm | |
| `query_stream_data_simple` | **L** | |
| `query_stream_data_aggregation` | **L** | |
| `query_stream_data_grouping` | **L** | |
| `query_stream_data_downsampling` | **L** | |

### StreamDataMetadataTools (2 tools)

_Low=2_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_archive_storage_stats` | **L** | |
| `get_rollup_query_metadata` | **L** | |

### TenantBackupTools (2 tools)

_Medium=2_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `dump_tenant` | **M** | |
| `restore_tenant` | **M** | |

### TenantManagementTools (8 tools)

_High=5 · Low=1 · Medium=2_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_tenants` | **L** | |
| `create_tenant` | **H** | |
| `delete_tenant` | **M** | |
| `clean_tenant` | **H** | |
| `attach_tenant` | **H** | |
| `detach_tenant` | **H** | |
| `clear_tenant_cache` | **H** | |
| `update_system_ck_model` | **M** | |

### TimeSeriesTools (11 tools)

_High=9 · Low=1 · Medium=1_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `enable_stream_data` | **H** | |
| `disable_stream_data` | **H** | |
| `activate_archive` | **H** — owner: confirm | |
| `disable_archive` | **H** | |
| `enable_archive` | **H** | |
| `retry_archive_activation` | **H** — owner: confirm | |
| `delete_archive` | **M** | |
| `list_rollups_for_archive` | **L** | |
| `freeze_rollup_archive` | **H** — owner: confirm | |
| `unfreeze_rollup_archive` | **H** | |
| `rewind_rollup_watermark` | **H** — owner: confirm | |

### ToolManagementTools (4 tools)

_Low=4_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `list_available_tools` | **L** | |
| `get_tool_details` | **L** | |
| `get_tool_statistics` | **L** | |
| `validate_tool_parameters` | **L** — owner: confirm | |

### UserManagementTools (7 tools)

_High=1 · Low=1 · Medium=5_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_users` | **L** | |
| `create_user` | **M** | |
| `update_user` | **M** | |
| `delete_user` | **M** | |
| `reset_user_password` | **H** | |
| `add_user_to_role` | **M** | |
| `remove_user_from_role` | **M** | |

### WorkloadTools (5 tools)

_High=3 · Low=1 · Medium=1_  ·  **Owner reviewed:** ☐

| Tool | Current | Notes for review |
|------|---------|------------------|
| `get_workloads_by_chart` | **L** | |
| `update_workload_chart_version` | **M** | |
| `deploy_workload` | **H** | |
| `undeploy_workload` | **H** | |
| `move_pipelines` | **H** | |
