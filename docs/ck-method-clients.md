---
description: Identity writes as CK method calls (generated clients), no REST fallback, sensitive parameters and the payload log filter.
applies_to: src/McpServices.MethodClients/**, **/{CkMethodTools,CkMethodInvocationPlanner,DynamicCkMethodClient}.cs, **/UserManagementTools.cs, **/RoleManagementTools.cs, **/ErrorTextRedactor.cs, **/McpPayloadLogFilter.cs, **/Identity{Method*,RtIds,ProviderMethods}.cs
---

# CK method calls and password tools

## 2a. CK method calls (generated method clients)

Operations that moved from REST to CK methods (CK v2 Phase 5, `octo-sdk/docs/method-client-migration-guide.md`) are
called through a **generated CK method client**, not through a REST method marked `[Obsolete]` (`CS0618` breaks the build).

- The clients are generated in `src/McpServices.MethodClients` (namespace
  `Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.*`): `construction-kits.yaml` imports the model
  range (today `System.Identity-[3.8,4.0)`, the identity wave 2 methods), `Meshmakers.Octo.ConstructionKit.MsBuildTasks` restores it and
  `Meshmakers.Octo.Sdk.SourceGeneration` emits the DTOs and `Rt{Type}MethodClient`. A project of its own because the
  generated public types carry no XML docs (CS1591 in `McpServices`). Add a model range there, never in `McpServices`.
- Build the tenant GraphQL client with `IdentityClientContext.TryBuildAsync(server, tenantId, withTenantClient: true)`
  (`ctx.TenantClient`, same token as `ctx.Client`; factory method `IOctoServiceClientFactory.CreateTenantClient`, asset
  repository endpoint `tenants/{tenantId}/GraphQL`).
- Methods address an entity by **rtId**; tools that take a user name or e-mail resolve it first
  (`GetUserByNameEmailOrId` → `UserDto.UserId`).
- Declared errors come back as the result (`!result.IsSuccess`, codes in `Rt{Type}MethodClient.{Method}Errors`): map them
  to a readable `ErrorMessage`. Platform errors (`METHOD_FORBIDDEN`, `METHOD_TARGET_NOT_FOUND`, …) are thrown as
  `CkMethodCallException`; report `"{Code}: {service message}"`.
- Tests mock `ToolTestBase.MockTenantClient` (`SendMutationAsync<It.IsAnyType>`): success answer, every declared error,
  one platform error — see `UserManagementToolsTests` (role tools).

| Tool | CK method | Still REST |
|------|-----------|------------|
| `add_user_to_role` | `System.Identity/User.AddRole-1` | — |
| `remove_user_from_role` | `System.Identity/User.RemoveRole-1` | — |
| `create_user` | `System.Identity/User.CreateUser-1` | — |
| `reset_user_password` | `System.Identity/User.ResetPassword-1` | — |


### Generic method calls: `invoke_ck_method`

`Tools/CkMethodTools.InvokeCkMethod` calls one method of any CK type that has no dedicated tool (typically an app method
such as a booking). It is a family-1 tool: the method runs only through the tenant's GraphQL endpoint, with the caller's
own token, so the method gateway of the asset repository decides about authorization (method roles, `allowSelf`, scopes,
data access), the target and the parameters exactly as for a GraphQL caller.

**Invariant.** The tool never does more than the same caller could do with one GraphQL method call on the same tenant. It
never dispatches through the engine, the dispatcher or the bus (it only reads the CK definitions from the tenant's CK
cache), never uses a service identity and never retries with another token. It may be narrower than GraphQL, never wider.

- **Flow.** Arguments are checked first (`CkMethodInvocationPlanner.ValidateArguments`, `ParseParameters`), then the tenant
  gate of the schema tools (`RuntimeSecurityContextResolver.ResolveTenantAccessAsync`) before the CK cache is read, then
  `CkMethodInvocationPlanner.Plan` builds the document, then
  `AssetClientContext.TryBuildAsync(server, tenantId, withTenantClient: true)` builds the tenant GraphQL client with the
  tenant-aware token (no fallback to the home token; a client for another tenant than the admitted one is refused) and
  `DynamicCkMethodClient` (a `CkMethodClientBase`) sends it.
- **Document.** The same mutation the generated SDK clients send:
  `mutation <Type><Method>($rtId: OctoObjectId!, $input: <DeclaringType><Method>MethodInput[!], $idempotencyKey: String)
  { runtime { <type>s { <method>(rtId, input, idempotencyKey) { invocationId result {…} } } } }` (`operation {…}` for an
  asynchronous method; record results select every attribute that is not hidden, secrets as `{ isSet keyMissing setAt }`).
  Every name comes from the resolved CK definition and must match `^[_A-Za-z][_0-9A-Za-z]*$`; `rtId`, the parameters and
  the idempotency key only go into the variables.
- **Refused before any call** (judged from the resolved definition, never from the input text; fail closed when the
  definition cannot be resolved): a method of a System model (the model name of the concrete type or of the method's
  declaring type starts with `System` in any case, also when a custom type inherits it or another spelling resolves to it
  — those methods have dedicated tools with their own risk class and confirm gate), a method of a knowledge page type or
  a type derived from one (`KnowledgeWriteRefusal.ProtectedTypes`: knowledge pages are written only by the knowledge
  tools), a method with a `sensitive` result or a `sensitive` parameter, an abstract or unknown type, an unknown method id (the type's key, e.g. `Book` / `Book-2`, or the qualified id), a
  missing `rtId` for an instance method or an `rtId` for a static method, parameters for a method without parameters,
  parameters that are not one JSON object, nest deeper than 32 or exceed 256 KiB of UTF-8, an `rtId` that is not 24
  hexadecimal characters, an idempotency key outside 1 to 128 characters.
- **Result.** Success: `invocationId` and `result` (raw JSON) or `operation` (handle of an asynchronous method). A refusal of
  the server: `methodError` with `code`, `parameters` and `details` exactly as in the GraphQL error's `extensions`, plus
  `declared` (the method lists the code in its `errors`, e.g. `CONCURRENT_CHANGE`; false for platform codes such as
  `METHOD_FORBIDDEN` or a GraphQL validation code). Free texts pass `ErrorTextRedactor.Redact`; codes and parameters are
  passed on unchanged.
- **Known limitation.** No MCP tool reads CK operation results yet: the outcome of an asynchronous method (also a declared
  error such as `CONCURRENT_CHANGE` that arrives in the operation result) is read through the GraphQL `runtime.operations`
  query with the operation id; the success message says so.
- **Declared codes.** `CONCURRENT_CHANGE` is the recommended code a method declares when a conditional write lost against
  a concurrent call (e.g. a slot booked twice); it is a declared method code, not a platform constant.
- **Risk.** `[McpRisk(Medium)]`, the class of the generic CRUD write tools (`create_entity`, `update_entity`,
  `delete_entity`); no confirm gate, because the gateway authorizes and the System methods that have High tools are
  refused.
- **Tests.** `CkMethodToolsTests`: the declared code compared with the GraphQL wire answer of the same refused call, the
  document and variables, asynchronous and record results, and one test per abuse case (other tenant, method without
  rights, undeclared method, oversized and malformed parameters, injection into the document, System methods in any case,
  inherited or resolved through another spelling, knowledge page types, sensitive result or parameter, unauthenticated
  caller, undeclared codes, unresolvable definition).
### Identity wave 2: every identity write tool

Since System.Identity 3.8 every identity write tool of groups, roles, clients (create, update, delete, roles, groups,
overlays, mirrors), API scopes, API resources, client and API resource secrets, identity providers, e-mail domain group
rules, external tenant user mappings and admin provisioning runs a CK method; the reads (`get_*`,
`lookup_source_tenant_user`, the cross-tenant grant permissions) stay on REST. Tool names, parameters and response fields
are unchanged.

- **Helper.** `Tools/IdentityMethodCall.RunAsync` runs the lookup(s) and the method call and returns an
  `IdentityMethodOutcome` (success, value, error text, error code). Every `ErrorMessage` goes through
  `IdentityMethodCall.SafeText(text, secrets…)`: the secrets the tool was given are replaced by `[REDACTED]`, then
  `ErrorTextRedactor.Redact` runs.
- **No REST fallback** (decided for every wave 2 tool, with or without a secret; identity is rolled out first). A method
  the identity service does not offer - `METHOD_HANDLER_NOT_AVAILABLE`, a document the asset repository rejects in GraphQL
  validation (every error with `extensions.number`), or a target the identity service returns without runtime id - gives
  `IsSuccess=false` with a text that names identity services with System.Identity 3.8 (for the secret-carrying methods
  also the bus switch `Methods:Identity:EnablePasswordMethods`) and says nothing was changed. No REST call is made.
- **Refusals.** Declared errors and the thrown platform errors (`METHOD_FORBIDDEN`, `METHOD_VALIDATION_FAILED`,
  `METHOD_TARGET_NOT_FOUND`, …) are reported as `CODE: message (name=value, …)` (`IdentityMethodCall.Describe`; detail
  codes follow in brackets). `create_external_tenant_user_mapping` keeps its `errorCode` field
  (`SourceTenantNotPermitted`, `UnknownGroup`, `ExternalTenantUserMappingAlreadyExists`).
- **Done as before.** The declared errors the identity REST endpoints answered with success stay a success:
  `MEMBER_ALREADY_IN_GROUP` (add user/client to group), `MEMBER_NOT_IN_GROUP` and, for a user or a client runtime id,
  `MEMBER_NOT_FOUND` (remove from group), `CHILD_GROUP_ALREADY_IN_GROUP` / `CHILD_GROUP_NOT_IN_GROUP`, and `ROLE_NOT_FOUND`
  on `remove_client_from_role`.
- **Runtime ids** (`Tools/IdentityRtIds`). Clients by `GetClient` → `ClientDto.RtId`, roles by `GetRoleByName` →
  `RoleDto.Id`, API scopes and API resources by a GraphQL query of the generated `RtApiScopeDto` / `RtApiResourceDto`
  (their REST DTO has no runtime id). A missing target is `"… not found."` without a method call. Groups, users, rules,
  mappings and identity providers are passed by runtime id already. The role tools of groups and clients accept role ids;
  `IdentityRtIds.RoleNamesAsync` turns the ids of the tenant's roles into the role names the methods take.
- **Identity providers** (`Tools/IdentityProviderMethods`). The methods are declared on the abstract
  `IdentityProvider` and called on the client of the concrete type (`RtGoogleIdentityProviderMethodClient`, …): create
  and update by the type of the DTO the tool builds, delete by the stored type (`GetIdentityProvider`).
- **Results** (`Tools/IdentityMethodResults`). Overlay, mirror and provisioning result records are mapped to the DTOs the
  tools returned before. `create_external_tenant_user_mapping` reads the stored mapping (`GetExternalTenantUserMapping`)
  for its `mapping` field.
- **Secrets.** `create_client_secret` / `create_api_resource_secret` require `confirm=true` (as `reset_user_password`;
  without it the tool refuses before any call) and run `AddSecret`, which generates the secret on the server: the clear
  text appears only in `clearTextSecret`; `secretValue` is the stored value (Base64 SHA-256 of the UTF-8 clear text, as
  the identity service stores and lists it). `clientSecret` (client and identity provider tools) and `valueEncrypted`
  (update and delete secret tools) are marked `SENSITIVE:`; they go into the method call and are scrubbed from every
  error and message. The update and delete secret tools still echo the `valueEncrypted` they were given in their
  `secretValue` response field, as before; pass the stored value, not the clear text. `McpPayloadLogFilter` keeps the tool
  arguments out of the logs. No tool rotates mirror secrets.
- **Tests.** `ToolTestBase.CkMethods` (`CkMethodFake`) answers the method calls and records them (`OperationNames`,
  `RtIdOf`, `InputOf`). `IdentityMethodToolsTests` runs every write tool through: the operation and target, no REST write,
  `METHOD_FORBIDDEN` reported with nothing else called, the missing-method text without REST call, declared errors as
  `CODE: message (params)`, the success mappings, and canaries for the secret parameters; `ApiSecretToolsTests` the
  generated secret shown once and redacted echoes.

| Tool | CK method (`System.Identity/…-1`) |
|------|-----------------------------------|
| `create_group`, `update_group`, `delete_group`, `update_group_roles` | `Group.CreateGroup`, `UpdateGroup`, `DeleteGroup`, `SetRoles` |
| `add_user_to_group`, `remove_user_from_group`, `add_client_to_group`, `remove_client_from_group` | `Group.AddMember`, `RemoveMember` |
| `add_group_to_group`, `remove_group_from_group` | `Group.AddChildGroup`, `RemoveChildGroup` |
| `create_role`, `update_role`, `delete_role` | `Role.CreateRole`, `UpdateRole`, `DeleteRole` |
| `add_client_credentials_client`, `add_device_code_client`, `add_authorization_code_client` | `Client.CreateClient` (secret) |
| `add_scope_to_client` | `Client.UpdateClient` (secret-carrying, sends only `allowedScopes`) |
| `delete_client`, `add_client_to_role`, `remove_client_from_role`, `update_client_roles` | `Client.DeleteClient`, `AddRole`, `RemoveRole`, `SetRoles` |
| `apply_client_overlay`, `clean_client_overlays` | `Client.ApplyOverlayUris`, `CleanOverlayEntries` |
| `provision_client_in_existing_tenants`, `provision_client_in_tenant`, `unprovision_client_from_tenant`, `set_client_auto_provision` | `Client.ProvisionMirrors`, `ProvisionMirrorInTenant`, `RemoveMirror`, `SetMirrorAutoProvision` |
| `create_client_secret`, `update_client_secret`, `delete_client_secret` | `Client.AddSecret`, `UpdateSecret`, `RemoveSecret` (secret) |
| `create_api_resource_secret`, `update_api_resource_secret`, `delete_api_resource_secret` | `ApiResource.AddSecret`, `UpdateSecret`, `RemoveSecret` (secret) |
| `create_api_scope`, `update_api_scope`, `delete_api_scope` | `ApiScope.CreateApiScope`, `UpdateApiScope`, `DeleteApiScope` |
| `create_api_resource`, `update_api_resource`, `delete_api_resource` | `ApiResource.CreateApiResource`, `UpdateApiResource`, `DeleteApiResource` |
| `add_*_identity_provider`, `update_identity_provider` | `<Provider>IdentityProvider.CreateIdentityProvider`, `UpdateIdentityProvider` (secret) |
| `delete_identity_provider` | `<Provider>IdentityProvider.DeleteIdentityProvider` |
| `create_email_domain_group_rule`, `update_email_domain_group_rule`, `delete_email_domain_group_rule` | `EmailDomainGroupRule.Create…`, `Update…`, `Delete…` |
| `create_external_tenant_user_mapping`, `update_external_tenant_user_mapping`, `delete_external_tenant_user_mapping` | `ExternalTenantUserMapping.CreateMapping`, `UpdateMapping`, `DeleteMapping` |
| `create_admin_provisioning_mapping`, `provision_current_user_as_admin`, `delete_admin_provisioning_mapping` | `ExternalTenantUserMapping.ProvisionAdmin`, `ProvisionCurrentUser`, `RemoveAdminProvisioning` |

### Communication wave 3: the communication write tools

Since System.Communication 4.19 the communication write tools run a CK method of `System.Communication` on the tenant client
(`CommunicationClientContext.TryBuildAsync(…, withTenantClient: true)`); `construction-kits.yaml` imports
`System.Communication-[4.19,5.0)` and `System-[2.7,3.0)` (the communication types derive from `System/Configuration`). The
reads (`get_*`), the communication lifecycle and enable/disable tools, the workload resource tools and the service account
impersonation switch stay on REST. Tool names, parameters and response fields are unchanged.

- **Helper.** `Tools/CommunicationMethodCall.RunAsync` returns a `CommunicationMethodOutcome` (success, value, error text,
  error code, declared message, `IsUnavailable`); error texts pass `ErrorTextRedactor.Redact`.
- **Role.** Every method requires the role `CommunicationManagement` (and the scope `octo_api`); the REST endpoints let every
  `octo_api` caller through. A caller without the role now gets `METHOD_FORBIDDEN`.
- **No REST fallback.** A method the controller does not offer (a document the asset repository rejects because its schema
  lacks it, or `METHOD_HANDLER_NOT_AVAILABLE`) gives `IsSuccess=false` with a text that names communication controller
  services with System.Communication 4.19 and says nothing was changed; for `METHOD_HANDLER_NOT_AVAILABLE` it also names a
  controller restart (the handler is missing for a moment while the controller restarts).
- **Runtime ids.** Every id a method takes is checked first (`InvalidIdError`): a malformed id is refused before any call,
  instead of a GraphQL validation error of the asset repository.
- **Workloads.** `DeployableWorkload.Deploy`, `Undeploy` and `SetChartVersion` are declared on the abstract type and run on
  the client of the concrete type. `ResolveWorkloadKindAsync` reads the workload's `ckTypeId` with a GraphQL query and
  compares the version-less form (`System.Communication/Adapter-1`, `-2`, … are all `Adapter`); an unknown workload is
  `Workload '<id>' not found.` without a method call.
- **Results.** A method that always returns a record (`Pipeline.Execute`, `Pipeline.SetDebugging`) and answers success without
  it is a failure (`returned no result`). `move_pipelines` calls `Pipeline.MoveToAdapter` once per pipeline and builds the
  former per-item list, as the controller's REST facade does: every error of one pipeline (declared, unknown pipeline,
  `METHOD_TIMEOUT`, a success without the move record, …) is a failed item and the other pipelines still move.
  `METHOD_FORBIDDEN` ends the batch; a missing method ends it only before a pipeline was moved, later (a controller restart
  during the batch) it is a failed item, so the list still says which pipelines were moved. `cancel_queued_execution` maps the declared errors `EXECUTION_ALREADY_LEASED` / `QUEUED_EXECUTION_NOT_FOUND` to the
  outcomes `AlreadyLeased` / `NotFound` of a successful call, as before.
- **Sensitive parameters.** `pipelineDefinition` (`deploy_pipeline`) and `pipelineInput` (`execute_pipeline`,
  `dry_run_pipeline`) are sensitive method parameters, marked `SENSITIVE:` in their description
  (`CommunicationMethodCall.SensitiveParameterPrefix`); `CommunicationMethodCall.ScrubSensitive` removes the value and every
  line of eight or more characters of it from every error text of these tools, the method refusals and the exceptions outside
  the method protocol alike.
- **Tests.** `CommunicationMethodToolsTests` runs every tool: its operation, no REST write (`AssertNoCommunicationRestWrite`),
  `METHOD_FORBIDDEN`, the missing-method and restart texts, an invalid id, a missing result record, a platform error of one
  pipeline of a move list, and the scrubbed definition. `WorkloadToolsTests` covers the workload type with element versions
  `-1`/`-2`; the data flow, pipeline and queue tool tests check the method variables.

| Tool | CK method (`System.Communication/…-1`) |
|------|----------------------------------------|
| `deploy_pipeline` | `Pipeline.Deploy` (target = the pipeline) |
| `execute_pipeline`, `dry_run_pipeline` | `Pipeline.Execute` (`isDryRun` false / true) |
| `set_pipeline_debug` | `Pipeline.SetDebugging` |
| `move_pipelines` | `Pipeline.MoveToAdapter`, once per pipeline |
| `deploy_data_flow`, `undeploy_data_flow` | `DataFlow.Deploy`, `Undeploy` |
| `deploy_triggers`, `undeploy_triggers` | `PipelineTrigger.DeployAll`, `UndeployAll` (static) |
| `undeploy_deployment_site` | `DeploymentSite.Undeploy` |
| `deploy_workload`, `undeploy_workload`, `update_workload_chart_version` | `DeployableWorkload.Deploy`, `Undeploy`, `SetChartVersion` on `Adapter`, `AdapterPool` or `Application` |
| `cancel_queued_execution` | `AdapterPool.CancelQueuedExecution` |

### Stream-data archives: the archive write tools

Since System.StreamData 1.30 the archive write tools run a CK method of `System.StreamData` on the tenant client
(`StreamDataClientContext.TryBuildAsync(…, withTenantClient: true)`); `construction-kits.yaml` imports
`System.StreamData-[1.30,2.0)`. The reads (`list_rollups_for_archive`, `list_recompute_jobs`), the rollup check and repair
(`validate_rollups`, `repair_rollups`, `get_rollup_repair_status`), `enable_stream_data` / `disable_stream_data` and the
archive data export/import stay on REST. Tool names and parameters are unchanged; `backfill_rollup_archive` gains the response fields `IsNoOp` and `OperationId` (below).

- **Helper.** `Tools/ArchiveMethodCall.RunAsync` / `RunOnArchiveAsync` return an `ArchiveMethodOutcome` (success, value, error
  text, error code); error texts pass `ErrorTextRedactor.Redact`.
- **Role.** Every method requires the role `StreamDataAdmin` (scope `octo_api`), the same rule as the REST endpoints.
- **No REST fallback.** A method the asset repository does not offer (a document its schema rejects, or
  `METHOD_HANDLER_NOT_AVAILABLE`) gives `IsSuccess=false` with a text that names System.StreamData 1.30 and says nothing was
  changed.
- **Runtime ids.** The archive or rollup id is checked first (`InvalidIdError`).
- **Archive type.** The lifecycle and column methods are declared on the abstract `Archive` and run on the client of the
  concrete type. `ResolveArchiveKindAsync` reads the archive's `ckTypeId` (`systemStreamDataArchive`) and compares the
  version-less form; an unknown archive is `Archive '<id>' not found.` without a method call. The three generated clients
  implement `IArchiveMethodClient` (`McpServices.MethodClients/ArchiveMethodClients.cs`, partial declarations). The rollup
  tools call `RtRollupArchiveMethodClient` directly (an id that is no rollup ends with `METHOD_TARGET_NOT_FOUND`).
- **Backfill.** `backfill_rollup_archive` starts the asynchronous `RollupArchive.BackfillFromSource-2` and reads the operation
  (`runtime.operations`, at most 30 seconds) until it names the queued job (progress stage `QUEUED`, parameter `jobRtId`): the
  response carries that job in state `Pending` (`Running` when the first read already shows a later stage), as the REST
  answer did, or the final job when the operation already ended (`Failed` with its reason after
  `STREAMDATA_RECOMPUTE_FAILED`), or `Job = null` and `IsNoOp = true` when the source archive holds no data. When no job is
  named within the 30 seconds, the response has `Job = null`, `IsNoOp = false` and says the backfill was accepted and runs in
  the background. Every accepted backfill carries `OperationId`. A tenant without the archive query (no stream data) gets
  the same text as a missing method; another error of the type read comes back redacted as it is.
- **Tests.** `StreamDataArchiveMethodToolsTests` runs every tool: its operation, no REST call of the stream data client,
  `METHOD_FORBIDDEN`, the missing-method texts, a declared error with parameters, invalid ids, the archive type with element
  versions, and the backfill against a loopback asset repository with answers recorded from a stream-data tenant
  (`Tools/Fixtures/assetrepo-streamdata-answer-*.json`, from octo-sdk). `TimeSeriesToolsTests` keeps the per-tool arguments
  and messages on the method path.

| Tool | CK method (`System.StreamData/…`) |
|------|-----------------------------------|
| `activate_archive`, `disable_archive`, `enable_archive`, `retry_archive_activation` | `Archive.Activate-1`, `Disable-1`, `Enable-1`, `RetryActivation-1` on `RawArchive`, `TimeRangeArchive` or `RollupArchive` |
| `delete_archive` | `Archive.DeleteArchive-1` |
| `add_archive_column` | `Archive.AddColumn-1` |
| `freeze_rollup_archive`, `unfreeze_rollup_archive`, `rewind_rollup_watermark` | `RollupArchive.Freeze-1`, `Unfreeze-1`, `RewindWatermark-1` |
| `backfill_rollup_archive` | `RollupArchive.BackfillFromSource-2` (asynchronous, operation handle) |

**HTTP-triggered pipelines and re-registration (AB#10015).** `dry_run_pipeline` runs a pipeline triggered only by
`FromHttpRequest@2` (no `FromExecutePipelineCommand` trigger; one with both is dry-run through the execute trigger) with `pipelineInput` as the request body (`$.body`) and the caller as `$.principal`; the controller routes the dry run
to the HTTP trigger, which refuses a caller who could not call the route (token of another tenant, none of its
`requiredRoles`). `execute_pipeline` of an HTTP-only pipeline is refused by the controller (it runs only through its
route). Nodes outside the dry-run-honouring load nodes still reach the outside in a dry run (AB#10016). After a CK model
import the adapter registers again at the controller (seconds locally, about a minute on a cluster); `deploy_pipeline`
and `dry_run_pipeline` append `PipelineTools.AdapterReconnectHint` ("retry in a moment") to a "no live SignalR
connection" or "did not answer" error (`WithAdapterReconnectHint`).

**Password methods.** `create_user` and `reset_user_password` have **no REST fallback** (by decision): when the
asset repository rejects the document (GraphQL validation, every error with `extensions.number`) or answers
`METHOD_HANDLER_NOT_AVAILABLE` (identity too old or `Methods:Identity:EnablePasswordMethods=false`), the tool returns a message
that names the needed identity version (`UserManagementTools.CreateUserMinimumIdentityVersion` 3.5.57,
`ResetPasswordMinimumIdentityVersion` 3.5.52) and says nothing was changed. Refusals are the server's (declared errors and
`METHOD_FORBIDDEN` / `METHOD_TARGET_NOT_FOUND` as `"{Code}: {message}"`, followed by the detail codes in brackets —
`UserManagementTools.WithCode`; the failed rules of a `PASSWORD_POLICY_VIOLATION` arrive only as detail codes, e.g.
`[PasswordTooShort, PasswordRequiresUpper]`); the tools keep no copy of the password policy. Every exception text the user tools
return also passes `ErrorTextRedactor.Redact` (same patterns as the octo-cli `ErrorOutputRedactor`): an end-to-end
run showed the local identity answering the user lookup with a 500 developer exception page whose `HEADERS` section echoed
the caller's bearer token. Other tools still return `ex.Message` unredacted. `AddMcpPayloadLogFilter` adds its
rule per provider as well: `UseNLog()` registers a provider rule for `NLogLoggerProvider`, and a provider rule beats a
category rule without a provider (`McpPayloadLogFilterNLogTests` wires the logging like `Program.cs`).
`reset_user_password` still requires `confirm=true` and makes no call without it.

**Sensitive parameters.** A password parameter's `[Description]` starts with `UserManagementTools.SensitivePasswordDescription`
(`SENSITIVE: …`); the value goes only into the method call. Every `ErrorMessage` of these tools passes `UserManagementTools.Scrub`
(password → `[REDACTED]`), and the response never carries the password. The ModelContextProtocol SDK logs JSON-RPC payloads (tool
arguments) at `Trace`; `Program.cs` adds `McpPayloadLogFilter` (`Configuration/`), which drops every line below `Debug` from the
`ModelContextProtocol*` categories in Microsoft.Extensions.Logging, before NLog - a runtime `reconfigureLogLevel` to Trace cannot
bring the payloads back. Keep that filter for every new tool with a secret parameter. Tests: `UserManagementToolsTests` (password
section: method document and variables, no REST call, every declared error, refusals, user of another tenant, missing method,
echoed password redacted, a canary searched in the serialized response, `SENSITIVE:` descriptions) and
`Configuration/McpPayloadLogFilterTests` (a real in-process MCP server and client: without the filter the canary is in the Trace
log, with it in no line).

For `Bot` system-scoped one-offs (e.g., `reconfigure_log_level` dispatch), grab it via `server.Services.GetRequiredService<IOctoServiceClientFactory>()` directly — there is no helper because the call sites are too few.
