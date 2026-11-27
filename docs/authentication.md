---
description: Transport bearer authentication, endpoint scope policy, system API controllers, the in-band session token store and the cross-tenant token exchange.
applies_to: **/Program.cs, **/Configuration/**, **/SystemApi/**, **/Services/McpSession*.cs, **/Services/Tenant*.cs, **/Tools/AuthenticationTools.cs
---

# Authentication and session tokens

## Transport authentication

**Both MCP endpoints require a valid OAuth2 bearer token on the HTTP request.** `Program.cs`
registers the JWT bearer handler (`AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
.AddJwtBearer()`, configured by `ConfigureJwtBearerOptions` → Authority + ValidIssuer,
`ValidateAudience = false`) and runs `UseAuthentication()` / `UseAuthorization()` /
`UseOctoTenantAuthorization()` before mapping the transport; both `app.MapMcp(...)` calls carry
`.RequireAuthorization(McpAuthorizationPolicy.EndpointPolicyName)` — the endpoint policy of the next section.

Agent tokens (scope `octo_agent`) are refused with `403 AgentTokenNotAccepted` on every endpoint but the two MCP
endpoints: `UseOctoAgentTokenRefusal()` runs between `UseAuthentication()` and `UseAuthorization()`, so no controller
policy answers first, and `MapOctoMcpEndpoints()` marks only the MCP routes `.AllowAgentTokens()` (never mark another
endpoint; `docs/agent-tools.md`).

`MapObservability` (health/metrics) is intentionally left anonymous. The file-transfer controller is
`[AllowAnonymous]` at the routing level, because a request may present the one-time transfer key instead of a token; it
evaluates `McpAuthorizationPolicy.PolicyName` — the platform-scope policy `McpTransportPolicy`, never `McpEndpointPolicy`
of the MCP endpoints, so an agent token is refused — on the token itself before it
touches the store, and binds every entry to the principal that created it (`file-transfer.md`). Without the scheme and the
middleware the direct-engine (family-2/3) tools would serve tenant data from MongoDB with no token at all.

`UseOctoTenantAuthorization` (shared `TenantAuthorizationMiddleware` from octo-common-services)
validates the route `{tenantId}` against the token's `tenant_id` claim, for user tokens and for
client-credentials service tokens (no user `sub` claim): identity stamps a `tenant_id` claim on
client-credentials tokens and the middleware compares it against the route tenant, governed by
`TenantAuthorizationOptions.ServiceTokenEnforcement` = `Disabled` | `LogOnly` | `Enforce` (the default).
The tenantless `/mcp` endpoint has no route tenant to check; it still requires a valid token, and the in-tool
gate binds every service token to its tenant.

> `ValidateAudience = false` means *any* client-credentials client of this authority passes the transport
> check — see *Client-credentials tokens are tenant-bound* in `tenant-gate.md` for what stops it in the tools.

**The enforcement mode is operator-settable because this service binds the options.**
`Program.cs` calls `builder.Services.AddOctoTenantAuthorization(builder.Configuration)`
(section `TenantAuthorization`), so the knob is
**`OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT`**, plus
`OCTO_TENANTAUTHORIZATION__CROSSTENANTSERVICECLIENTIDS__0…` for the cross-tenant allow-list — the
identical section and variable names Identity, Communication Controller, Asset-Repo and Bot use, so
one fleet-wide value reaches all five. Calling `UseOctoTenantAuthorization()` **without** the
`Add…` call leaves the service on the built-in defaults, where the environment variable is inert. Keep the
`Add…` call. Semantics and rollout rules: `octo-common-services/docs/tenant-authorization.md`.

The mesh adapter mints **tenant-bound** tokens (its own `ServiceAccountConfiguration`) and the AI
service's per-session tokens are tenant-bound as well (`IMcpTokenIssuer` with
`acr_values=tenant:{tenantId}`), so they pass the match on their own.

**One default allow-list entry: the AI service's client.** The knowledge platform update
runner of octo-ai-services mints **one system-tenant token** and calls
`knowledge_adopt_platform_updates` on `/{tenantId}/mcp` of every AI tenant (by decision: an explicit entry, no blanket exemption). `Configuration/ConfigureTenantAuthorizationOptions`
(an `IPostConfigureOptions<TenantAuthorizationOptions>` registered right after
`AddOctoTenantAuthorization`) appends `McpServiceOptions.AiServiceClientId` (`Mcp:AiServiceClientId`,
`OCTO_MCP__AISERVICECLIENTID`, default `octo-ai-adapter` = the client the AI service registers in the
system tenant itself) to `CrossTenantServiceClientIds`. Rules: exact id only (a value with `*` throws
`OptionsValidationException` at startup, `ValidateOnStart` - a pattern or a lone `*` would exempt every
client); operator entries from `OCTO_TENANTAUTHORIZATION__CROSSTENANTSERVICECLIENTIDS__<n>` stay and
the id is not added twice (case-insensitive); empty drops the default entry and its guard (an entry an operator
sets for the same id is then the operator's alone and unguarded on the route; the in-tool gate still refuses
foreign tenants). The default lives here, not in octo-common-services, whose list stays empty for every other
host. An allow-listed token also skips the route gate's tenant generation check, like any
allow-listed client; for the trusted pair that means a token of a deleted and re-created system tenant is not
refused there (the system tenant is not re-created in practice). Registration: `AddAiServiceClientTenantTrust()`
and `UseAiServiceClientTenantGuard()` (`Configuration/AiServiceClientTenantTrustExtensions`), the same calls in
`Program.cs` and in the tests.

🔴 **The entry is bound to the trusted pair by `Configuration/AiServiceClientTenantGuard`.** The shared
gate matches the allow-list by `client_id` alone (case-insensitive), and client ids are unique only per
tenant: a tenant administrator could create a client `octo-ai-adapter` in their own tenant and its token
would pass every other tenant's route. The in-tool gate would still refuse its tool calls
(`tenant-gate.md`), but the route gate must not open wider than the trust. The guard runs right before
`UseOctoTenantAuthorization()`, reads the caller with the same `McpCallerPrincipal` as the in-tool gate and
answers 403 for a service token whose client id the entry would admit but that is not the trusted pair
(`Services/AiServiceClientTrust.IsTrustedPair`: exact id, token of `System:SystemTenantId` - the same helper
`RuntimeSecurityContextResolver` uses), when it addresses a foreign route tenant; on its own tenant's route
such a client works like any tenant client. It acts only while `ServiceTokenEnforcement` is `Enforce` (the
default); in `LogOnly`/`Disabled` the route gate admits every service token anyway. Keep the guard whenever the
entry exists.

Tests: `Configuration/ConfigureTenantAuthorizationOptionsTests` (option rules, plus a TestServer with the
guard and the real `UseOctoTenantAuthorization`: `octo-ai-adapter` with a system-tenant token gets 200 on
another tenant's route, another client 403, the same id created in another tenant 403 on a foreign route
and 200 on its own, a case variant of the id from the system tenant 403, a token without `tenant_id` 403, the
guard steps aside in `LogOnly`, 403 with the entry switched off, a `*` pattern stops the start) and `Services/AiServiceClientTrustTests` (the shared pair).

The transport gate only sees the **route** tenant. The per-tool-param cross-tenant hole it leaves
open is closed by `RuntimeSecurityContextResolver` (`tenant-gate.md`).

## Endpoint scope policy

`Configuration/McpAuthorizationPolicy` registers two policies:

- `McpTransportPolicy`: `RequireAuthenticatedUser()` **plus** at least one of the scopes in
  `McpServiceOptions.RequiredApiScopes` on the `scope` claim. The REST controllers of this service
  (system diagnostics, file transfer) require it.
- `McpEndpointPolicy`: the same, **or** a client token (no `sub`) with the agent scope `octo_agent`
  (`docs/agent-tools.md`). Only the two `MapMcp` endpoints require it, because only there does every
  agent call pass the mandate guard and the token exchange. Never put `McpEndpointPolicy` on a
  controller: an agent token would reach it without mandate check, kill switch or tenant check.

Before this they carried a bare `RequireAuthorization()`, so a
token with no Octo API scope at all (a front-end `openid profile` token, say) reached every tool —
while every backend service gates on `scope`.

**The requirement is uniform, and it is the write scope `octo_api`.** MCP multiplexes all tools
over one JSON-RPC `POST`; the tool name lives in the request *body* and ASP.NET authorization runs on
the endpoint before the body is read, so there is no second endpoint to hang a stricter policy on and
no way to split read from write there. Accepting `octo_api.read_only` would therefore hand a
read-only token the whole write surface (`delete_tenant`, `uninstall_blueprint`, …) — strictly worse
than refusing it. Nothing breaks: the two seeded MCP clients (660…33 / 660…34) allow `octo_api` and
*not* `octo_api.read_only`, `AuthenticationTools`' device flow and `TenantTokenExchanger` both request
`octo_api`, and the mesh-adapter service account requests `ApiScopes.OctoApiFullAccess`.

`RequiredApiScopes` is configurable (`Mcp:RequiredApiScopes`) so an operator can admit a service
account provisioned with a different scope without a code change; an empty list restores the
earlier authenticated-only behaviour. The check accepts both wire encodings of `scope` — one
claim per scope (what the backend services' `RequireClaim` policies rely on) and a single
space-delimited value — which is never more permissive than the backend rule, it just avoids
refusing a correctly-scoped token over claim splitting.

A per-tool read/write distinction is still possible **in band**, at tool dispatch where the tool name
is known; that is a separate change from the endpoint policy. `WithHttpTransport()`'s session mode is
untouched.

## The REST controllers are not the MCP transport

`app.MapControllerRoute("default", …)` also serves the attribute-routed controllers under
`SystemApi/`, and **nothing in `Program.cs` gates them** — `RequireAuthorization(McpEndpointPolicy)`
sits on the two `MapMcp` endpoints only. Whatever protects a controller here has to be on the
controller.

`SystemApi/v1/Controllers/DiagnosticsController` shipped with both of its `[Authorize]` attributes
**commented out**, so `POST system/v1/diagnostics/reconfigureLogLevel` was anonymous: anyone who could
reach the pod could reconfigure NLog for the whole process — every logger to Trace (disk and
log-pipeline denial of service, plus disclosure on a server that brokers tenant data), or all of them
silenced. Every sibling service gates the identical endpoint.

It now carries `[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]` on the
class and `[Authorize(McpAuthorizationPolicy.PolicyName)]` on the action:

- **The policy is the platform-scope policy `McpTransportPolicy`**, not one invented for this
  controller, and never `McpEndpointPolicy`. It is `RequireAuthenticatedUser` plus one of
  `Mcp:RequiredApiScopes` (default `octo_api`) — the same platform write scope the sibling services'
  system diagnostics endpoints require. A caller that can already invoke `delete_tenant` over the
  transport gains nothing new; a token without the platform scope does not reach this controller. An
  agent token (`octo_agent`) reaches only the MCP endpoints, behind the mandate guard.
- 🔴 **Do not uncomment `[Authorize(Constants.SystemApiPolicy)]` verbatim.** That constant is
  registered *nowhere* — `AddAuthorization` only registers `McpTransportPolicy` and `McpEndpointPolicy` — so it would throw
  `InvalidOperationException: The AuthorizationPolicy named: 'SystemApiPolicy' was not found` on every
  request instead of authorizing one. The constant is still in `Constants.cs` and is dead.
- **The scheme is pinned** because this host's default *challenge* scheme is the MCP one, which
  answers an unauthenticated request with RFC 9728 protected-resource metadata — the wrong answer for
  a plain REST endpoint.
- Tests: `tests/McpServices.Tests/Configuration/DiagnosticsControllerAuthorizationTests.cs`.

## In-band session token (outbound calls) — bound to the caller

Separate from the inbound gate, family-1 tools use a per-session token for their **outbound** calls
to the backend services:

1. **OAuth Device Authorization** — `authenticate` issues a device code; user logs in via browser;
   `check_auth_status` polls until tokens are issued; tokens go into `IMcpSessionTokenStore`.
2. **Per-request token injection** — `McpSessionContext.ResolveAccessTokenAsync(server[, tenantId])`
   picks the token; the `*ClientContext` helpers feed it to
   `OctoServiceClientFactory.Create*Client(tenantId, accessToken)`.

This store decides **which identity Identity / Asset-Repo / Communication / Reporting / StreamData /
Bot see**, so it is bound to the authenticated caller in two independent ways. Both live in
`Services/McpSessionContext.cs`.

**1. The key is derived from the request principal, not from the client.**
`McpSessionContext.TryGetSessionKey` returns `u:{tenant_id}:{sub}` for a user principal, `c:{client_id}`
for a client-credentials one, optionally suffixed `|{Mcp-Session-Id}` when the client sent that header.
Consequences:

- **No shared slot.** The old key was the `Mcp-Session-Id` header alone with a constant
  `"default-session"` fallback. Since `WithHttpTransport()` runs at its default
  `HttpServerSessionMode.Stateless`, the server never mints that header and no client sends one — so in
  practice *every* caller on the pod shared one process-wide slot, and whoever ran `authenticate` last
  donated their identity (cross-tenant included) to everyone else.
- **The header is no longer an identity.** A caller may still send any value; it only partitions
  *within* their own namespace, so it can never address someone else's entry.
- **No principal ⇒ no store.** `TryGetSessionKey` returns null and the call falls back to the request's
  own `Authorization: Bearer` — which is exactly the right identity. `authenticate` /
  `check_auth_status` refuse outright in that case (there is no slot to park a device code in that
  isn't shared). Both endpoints require a validated bearer, so a principal is always
  present in production. `McpSessionContext.GetCallerLabel` is the deliberately non-null variant, used
  **only** for the file-transfer ownership tag of the tools. The HTTP endpoints of the file transfer compare
  the owner key (`TransferOwnerKey`: the `StoreKeyPrefix` of a user, `c:<client_id>:<tenant>` for a client) instead, because a plain PUT or GET carries no `Mcp-Session-Id`
  (`file-transfer.md`).

**2. A stored token is verified before it is used.** `VerifyBinding` requires the token's `sub` **and**
`tenant_id` to equal the request principal's; an opaque token fails because it cannot be bound at all.
The failure is a hard refusal with `Constants.SessionTokenNotBoundError` (`…does not belong to the
authenticated caller…`), *not* a silent downgrade to the header bearer and *not* `Not authenticated` —
the caller is authenticated, their stored session simply carries a different identity. The check is
re-applied to a refreshed token, so an expired foreign token cannot be laundered into a fresh one.

> **Client-credentials principals are exempt from check 2.** A service token has no `sub`, so the
> subject match is not a question that can be asked of it. The AI Adapter worker (`IMcpTokenIssuer`)
> and the mesh-adapter `AnthropicAiQueryNode` (`ServiceAccountConfiguration`) present their token in
> the `Authorization` header and never populate the store; requiring a subject would lock both out of
> every tenant. Their store namespace is their `client_id`. The tenant they may act on is decided by the
> tenant gate (*Client-credentials tokens are tenant-bound*), not here.

**Device flow under the binding.** `authenticate` and `check_auth_status` land on the same key because
the principal is the same across the two calls. But a device login against a **different** tenant
produces a token for that tenant's shadow user, which check 2 then refuses — `check_auth_status` says
so in its success message instead of letting the next tool call fail with an unrelated-looking error.
The sanctioned way to reach another tenant is `switch_tenant` / the transparent RFC 8693 exchange.

**Not changed:** `WithHttpTransport()` still runs in the default stateless mode. Switching it to
`Stateful` would make the transport mint and bind `Mcp-Session-Id` itself, which is a behaviour change
for every client (session affinity required, GET/DELETE endpoints appear) — evaluate separately.

Tenant comes from (in order):
1. Explicit `tenantId` tool parameter
2. Route parameter `{tenantId}` on the `/{tenantId}/mcp` endpoint
3. Error from `ITenantResolutionService.ResolveTenantId(...)`

Never store tenant state on the session. Stateless multi-tenancy is the design.

## Cross-tenant token exchange

The backend `TenantAuthorizationMiddleware` authorizes the route tenant strictly against the token's
`tenant_id` claim (NOT `allowed_tenants`), so one access token acts on exactly one tenant. To operate
on a different tenant B without a device re-auth, the five **tenant-routed** `*ClientContext` helpers
call `McpSessionContext.TryGetAccessTokenAsync(server, tenantId)`, which transparently exchanges the
home token for a B-scoped token (RFC 8693 token-exchange grant → `POST /connect/token`
`grant_type=urn:ietf:params:oauth:grant-type:token-exchange`, `subject_token`=home token,
`acr_values=tenant:B`, `client_id=octo-mcpServices-device`) via `ITenantTokenExchanger`, cached
per-`(sessionKey, tenantId)` in `McpSessionTokenStore` — the same caller-bound `sessionKey` as the home
token, so a cached B token is reachable only by the principal that obtained it, and the home
token used as `subject_token` has already passed the binding check. The identity side re-resolves roles in B (issues
the token for the B-shadow user) so there is no role leak. The `switch_tenant` tool is the explicit
affordance; on failure it recommends the `authenticate` device-flow fallback.

**Opaque-token safety:** the overload exchanges ONLY when the home token's `tenant_id` is readable AND
differs from the target. A token whose `tenant_id` cannot be read (opaque, non-JWT) keeps using the
home token, and the backend decides. A service token whose `tenant_id` equals the target tenant is used as-is; a service token bound to another tenant (for example the allow-listed AI service client) goes through the exchange like a user token. **Bot stays on the
home token** — its client is NOT tenant-routed (`CreateBotClient` takes no `tenantId`), so an exchange
there is unnecessary and could break bot ops the home token already serves.

**Tools that build their client without a `*ClientContext`.** `TenantManagementTools`
(`get_tenants`, `create_tenant`, `delete_tenant`, and `clean_tenant` / `attach_tenant` / `detach_tenant` /
`clear_tenant_cache` / `update_system_ck_model` via `TryBuildContext`) and `export_runtime_graphql_sdl`
used to take the **home** token (`ResolveAccessTokenAsync(server)`) and route it to the parent/route
tenant, so under user-token Enforce a caller homed elsewhere got 403 on a parent tenant. They now resolve
the route/parent tenant first and call `ResolveAccessTokenAsync(server, tenantId)` (same RFC 8693 path
as octo-cli). A failed exchange returns `IsSuccess = false` with the session error
(`Constants.TenantExchangeFailedError(tenantId)`) and makes **no** SDK call — never a fallback to the home token.
The caller needs access to the addressed (parent) tenant itself; being admin of the child is not enough.
Any new tool that routes an SDK/HTTP client to a tenant must use the tenant overload; the home-token
overload is only for tenant-less calls (whoami, file transfer, Bot).

> **Interactive-client note:** because the transport now requires an inbound bearer, a purely
> interactive client that previously relied only on the in-band device flow must present a bearer
> token to connect. The production clients (AiWorker, mesh-adapter) already do. The
> `ConfigureJwtBearerOptions` contract is pinned by `Configuration/ConfigureJwtBearerOptionsTests`;
> the endpoint-gating itself has no in-process HTTP test (the host needs MongoDB/RabbitMQ) — verify
> against a running identity service.
