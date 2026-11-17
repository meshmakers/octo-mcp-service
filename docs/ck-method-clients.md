### 2a. CK method calls (generated method clients, AB#8440)

Operations that moved from REST to CK methods (CK v2 Phase 5, octo-sdk `docs/method-client-migration-guide.md`) are
called through a **generated CK method client**, not through a REST method marked `[Obsolete]` (`CS0618` breaks the build).

- The clients are generated in `src/McpServices.MethodClients` (namespace
  `Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.*`): `construction-kits.yaml` imports the model
  range (today `System.Identity-[3.2,4.0)`), `Meshmakers.Octo.ConstructionKit.MsBuildTasks` restores it and
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
| `create_user` | `System.Identity/User.CreateUser-1` | — (AB#8418) |
| `reset_user_password` | `System.Identity/User.ResetPassword-1` | — (AB#8418) |

**Password methods (AB#8418).** `create_user` and `reset_user_password` have **no REST fallback** (decision AB#7039): when the
asset repository rejects the document (GraphQL validation, every error with `extensions.number`) or answers
`METHOD_HANDLER_NOT_AVAILABLE` (identity too old or `Methods:Identity:EnablePasswordMethods=false`), the tool returns a message
that names the needed identity version (`UserManagementTools.CreateUserMinimumIdentityVersion` 3.5.57,
`ResetPasswordMinimumIdentityVersion` 3.5.52) and says nothing was changed. Refusals are the server's (declared errors and
`METHOD_FORBIDDEN` / `METHOD_TARGET_NOT_FOUND` as `"{Code}: {message}"`, followed by the detail codes in brackets —
`UserManagementTools.WithCode`; the failed rules of a `PASSWORD_POLICY_VIOLATION` arrive only as detail codes, e.g.
`[PasswordTooShort, PasswordRequiresUpper]`); the tools keep no copy of the password policy. Every exception text the user tools
return also passes `ErrorTextRedactor.Redact` (same patterns as the octo-cli `ErrorOutputRedactor`): the AB#8418 E2E
run showed the local identity answering the user lookup with a 500 developer exception page whose `HEADERS` section echoed
the caller's bearer token. Other tools still return `ex.Message` unredacted (AB#8972). `AddMcpPayloadLogFilter` adds its
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
