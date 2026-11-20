---
description: Identity writes as CK method calls (generated clients), no REST fallback, sensitive parameters and the payload log filter.
applies_to: src/McpServices.MethodClients/**, **/UserManagementTools.cs, **/RoleManagementTools.cs, **/ErrorTextRedactor.cs, **/McpPayloadLogFilter.cs, **/Identity{Method*,RtIds,ProviderMethods}.cs
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
- **Secrets.** `create_client_secret` / `create_api_resource_secret` run `AddSecret`, which generates the secret on the
  server: the clear text appears only in `clearTextSecret`; `secretValue` is the stored value (Base64 SHA-256 of the
  UTF-8 clear text, as the identity service stores and lists it). `clientSecret` (client and identity provider tools) and
  `secretValue` go only into the method call and are scrubbed from every text. `McpPayloadLogFilter` keeps the tool
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
