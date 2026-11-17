### Caller identity + tenant gate for direct-engine tools (AB#5030)

Every family-2/3 tool used to open a **system session** (`tenantRepository.GetSessionAsync()`
parameterless), which bypassed data-permission enforcement (AB#4969) completely and let any
authenticated caller name any tenant in the `tenantId` tool parameter. `Services/RuntimeSecurityContextResolver.cs`
is the single choke point that fixes both:

```csharp
var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
var security = await RuntimeSecurityContextResolver.ResolveAsync(server, tenantResolution, tenantId);
if (security.Error != null)
{
    return new MyResponse { IsSuccess = false, ErrorMessage = security.Error };
}

var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
using var session = await tenantRepository.GetSessionAsync(security.SecurityContext!);
```

#### Identity comes from the request principal, never from the session store

The caller identity is read off `IHttpContextAccessor.HttpContext.User` — the principal the JWT
bearer handler produced *after* it verified signature, issuer and lifetime. `Services/McpCallerPrincipal.cs`
is the one reader of those claims (AB#5036 extracted it here so the tenant gate and the session-token
binding cannot disagree on what a "service token" is).

**It must not come from `McpSessionContext.TryGetAccessTokenAsync`.** The store is a *cache of
tokens*, not a statement about who is calling; since AB#5036 its key is derived from this very
principal, so reading an identity back out of it would be circular. It is consulted **only** on the
cross-tenant path, and there the token it hands out is bound back to the request principal before it
is used (below).

Claim reading mirrors `AssetRepositoryServices/GraphQL/Helpers.GetSecurityContext` one-for-one, so
the same user sees the same data through MCP and through GraphQL:

- **Subject** — `sub` → `ClaimTypes.NameIdentifier` → `client_id`. `ConfigureJwtBearerOptions`
  leaves `MapInboundClaims` at its ASP.NET default of `true`, so a JWT `sub` reaches the principal
  as `ClaimTypes.NameIdentifier`; probing only `"sub"` would misclassify every user token as a
  service token and put it on the service-token path below.
- **Roles** — the union of each identity's `RoleClaimType` and the JWT short name `"role"`,
  de-duplicated. Same reason: inbound mapping may or may not have renamed them.
- **Tenant** — `tenant_id` is not in the inbound claim map, so it keeps its JWT name.

An **unauthenticated principal is a denial**, not a claimless context: `Not authenticated`, no
session opened. Same for a host that never registered `IHttpContextAccessor` (resolved with
`GetService`, so it degrades into a denial rather than an exception out of a tool).

#### The three outcomes

1. **Service (client-credentials) principal** — no subject at all, only `client_id`. Needs the exact
   tenant match (`tenant_id` == resolved tenant) like a user; the only exception is the configured AI
   service client with a token of the system tenant. Context is `ForUser(client_id, roles)`; never the
   cross-tenant exchange. See *Client-credentials tokens are tenant-bound (AB#9189)* below.
2. **User principal, `tenant_id` == resolved tenant** (the normal case) — context is
   `ForUser(sub, roles)` straight from the validated principal. **The token store is not read at
   all** on this path (pinned by `ResolveAsync_SameTenant_DoesNotConsultTheSessionTokenStore`).
3. **User principal, `tenant_id` != resolved tenant** — the cross-tenant path, below.

A user principal whose token carries **no `tenant_id` claim** is refused outright
(`…denied: the caller's token carries no 'tenant_id' claim.`). `TenantAuthorizationMiddleware`
answers 403 for exactly that shape on the route gate, and the per-tool tenant parameter must not be
more permissive than the route.

**Never `RtSecurityContext.System`** — the whole point of AB#5030 is that these tools act as the
caller. **Never throws** — every failure (unresolvable tenant, missing token, unreachable identity,
failed exchange) comes back as `RuntimeSecurityContextResult.Error` so the "never throw out of a
tool" rule holds.

#### Cross-tenant path (AB#4338)

A user homed in tenant A legitimately reaching tenant B goes through the RFC 8693 exchange. Three
things happen in order, and each one can deny:

1. **Bind the store token to the request.** The home token is fetched from the session store and
   only accepted when its `sub` **and** `tenant_id` match the request principal's. An unparseable
   (opaque) home token fails this too. Otherwise: `…denied: the stored session token does not belong
   to the authenticated caller.` Since AB#5036 this check lives in `McpSessionContext` (constant
   `Constants.SessionTokenNotBoundError`) and applies to **all three tool families**, so the resolver
   just propagates the message; client-credentials principals never reach this path (see below).
2. **Exchange.** `McpSessionContext.TryGetAccessTokenAsync(server, tenantId)` performs / caches the
   exchange. A `null` result gets its **own** message — `…denied: the cross-tenant token exchange
   failed. The caller is homed in tenant 'A' and has no permission for 'B'…` — deliberately **not**
   `Not authenticated`, which would send AI clients into a pointless device-flow re-login.
3. **Verify the exchanged token.** Its `tenant_id` must read back as the target tenant, else
   `…denied: the session token is issued for tenant 'Y'.` (`<unreadable>` when opaque). Reading
   these claims without signature validation is sound: the token came straight from the identity
   server over TLS as the answer to our own exchange request and never passed through the caller.

On success the context is built from the **exchanged** token — `ForUser(shadow sub ?? client_id,
shadow roles)`. The identity in B is the B-shadow user, so reusing the A principal here would leak
A's roles into B.

#### Client-credentials tokens are tenant-bound (AB#9189)

Until AB#9189 the in-tool gate exempted every client-credentials token (no `sub`): it returned
`ForUser(client_id, roles)` for whatever tenant the tool parameter named. Because `ValidateAudience = false`,
any client of this authority with scope `octo_api` passed the transport gate, so a tenant admin of A could
create such a client in A and read and write B's runtime data (CRUD including `*_secrets`, aggregation,
stream data, schema discovery, knowledge) — on `/A/mcp` with `tenantId=B` (the tool parameter wins over the
route in `TenantResolutionService`, and the HTTP gate only sees the route) and on the tenantless `/mcp` (no
route tenant to check). The family-1 tools were not affected (the backend enforces the tenant).

Now `RuntimeSecurityContextResolver.ResolveServicePrincipal` applies the **exact tenant match** to service
tokens too:

| Token | Target tenant | Result |
|---|---|---|
| service token with `tenant_id` = target | any | allowed, `ForUser(client_id, roles)` |
| service token without `tenant_id` | any | denied (`…carries no 'tenant_id' claim`) |
| service token of tenant A | B | denied (`…is issued for tenant 'A'. A client-credentials token can only act on its own tenant…`) |
| client `Mcp:AiServiceClientId` (default `octo-ai-adapter`, exact, case-sensitive) issued for `System:SystemTenantId` | any | allowed — the **only** cross-tenant pair |
| same client id issued for any other tenant | another tenant | denied (client ids are unique per tenant only) |

- No pattern, no list from outside without the issuer. `OCTO_MCP__AISERVICECLIENTID` replaces the id; empty
  switches the exception off. When the options cannot be resolved at all the call is denied (fail closed);
  `Program.cs` always registers both (an unbound `System` section means the default system tenant id
  `OctoSystem`). A service token never takes the cross-tenant exchange path.
- On the trusted pair the tools act as `ForUser(octo-ai-adapter, <roles issued in the system tenant>)` inside the
  target tenant, whose data permissions check those roles by name. Keep the roles of that client minimal.
- Tenant-bound callers keep working: the AI worker (`IMcpTokenIssuer`, one token per tenant with
  `acr_values=tenant:{tenantId}`), the mesh-adapter node (its tenant's service account) and agent clients that
  request one token per tenant (e.g. `claude-agent-<user>-<host>` with `acr_values tenant:<id>` on
  `/<id>/mcp`).
- **Every decision is logged** (client id, token tenant, target tenant, outcome, reason; allow at
  Information, deny at Warning; no token content) and counted in
  `octo.tenant_auth.mcp_service_token_decisions{outcome, reason}` (meter `Meshmakers.Octo.TenantAuthorization`,
  exported by `AddObservability()`; `McpServiceTokenDecisionMetrics`). Reasons: `same_tenant`,
  `ai_service_client_of_system_tenant`, `tenant_mismatch`, `no_tenant_claim`.
- The **HTTP** gate's shared `CrossTenantServiceClientIds` allow-list (octo-common-services) still matches
  `client_id` without the issuer tenant. This service adds only `Mcp:AiServiceClientId` to it and binds that
  entry to the same trusted pair with `AiServiceClientTenantGuard` (AB#9188, section *One default allow-list
  entry* above); both gates decide with `AiServiceClientTrust.IsTrustedPair`. Pairing the shared list with the
  issuer for every host is the follow-up of AB#9189.

> Careful with "tenant gate" — there are two. The **HTTP** one (`TenantAuthorizationMiddleware`,
> route `{tenantId}` vs. `tenant_id` claim) is the one AB#5032 staged behind
> `ServiceTokenEnforcement`; since AB#5047 this service binds that setting, which defaults to `Enforce`
> since AB#5077. The **in-tool** one described in this section (`RuntimeSecurityContextResolver`, which
> guards the tenant named in a *tool parameter*) is independent of that variable and always enforces the
> table above. The endpoint half of AB#5032 (scope `octo_api` required) still applies in front of both.

#### Call sites

`ResolveTenantAccessAsync` is the same call for sites that never open a session — `SchemaDiscovery
Tools`, `StreamDataMetadataTools`, `CkSchemaResources`, `StreamDataContext.TryResolveAsync`,
`EchoTool` — those only read `.Error`. It deliberately delegates to `ResolveAsync` rather than
reimplementing a cheaper gate-only variant, so the two can never drift apart.

`EchoTool` **is** gated (it used to be exempt). Ungated it was a tenant-existence oracle: any
authenticated caller could probe arbitrary tenant ids and tell "exists" from "does not exist" by the
shape of the answer. Its tenant lookup is now also wrapped in a `try/catch` — it previously threw
out of the tool on an unknown tenant.

### Stream-data CK-type permission gate (AB#5038)

`Services/DataPermissionStreamGuard` is the MCP mirror of the asset-repo GraphQL
`GraphQL/Utils/DataPermissionStreamGuard` (AB#4973, decision F4). Same three short-circuits in the
same order (system context → services not wired → `!policyTable.HasRules`), same
`RtDataAccessEvaluator.Classify(..., RtDataAction.Read, ..., includeAuditOnlyPolicies: false)`, same
rejection condition **`Denied or OwnedOnly`**, and the same message text verbatim:

```
Access denied: missing data permission 'Read' on '<SemanticVersionedFullName>' for stream data.
```

Keeping the wording identical is the point — a caller must get the same answer through MCP and
through GraphQL, and `StreamDataPermissionGateTests` pins the literal string. The one deliberate
difference is the shape: the GraphQL guard throws `ExecutionError`; MCP tools never throw, so the
guard **returns** the message and the call site puts it into `ErrorMessage`.

An **owner-scoped grant denies** stream reads. Time-series rows carry no creator, so row-level
filtering is impossible for them — the conservative accepted limitation F4 of AB#4969. AuditOnly
policies never block.

Call sites:

- `StreamDataContext.TryResolveAsync` — the shared archive-resolution path, so all four transient
  query tools are covered at once. It now calls `ResolveAsync` (not `ResolveTenantAccessAsync`)
  because it needs the caller's `RtSecurityContext`, not just the deny decision.
- `execute_stream_data_query` — guards on the persisted `QueryCkTypeId`, mirroring asset-repo's
  `StreamDataQuery` resolver (which likewise does *not* read it off an archive snapshot).
- `StreamDataMetadataTools` — `get_archive_storage_stats` (row counts and table sizes describe the
  rows), `get_rollup_query_metadata` (the logical source paths describe the CK attributes) and
  `resolve_series_query` (which archive holds the series at which grain).

Two shape notes on the metadata tools. `get_archive_storage_stats` is **fail-closed on the batch**: a
single protected archive refuses the whole call rather than blanking one row, because the response is
index-aligned with the caller's input list and a blanked row is indistinguishable from "table does not
exist yet". And it only resolves archive snapshots when `DataPermissionStreamGuard.IsEnforcingAsync`
says the tenant has policies — on the (overwhelmingly common) unprotected tenant it stays the single
`GetArchiveStatsAsync` round-trip it has always been. `IsEnforcingAsync` is cheap to pair with the
follow-up check because `IDataPermissionResolver` TTL-caches the policy table per tenant (60 s).

The guard loads the tenant CK cache (`LoadCacheForTenantAsync`) before classifying, but **only on the
protected path**: `RtDataPermissionCkTypeHelper.GetSelfAndBaseFullNames` can only walk the base-type
chain from a hydrated cache, and a cold cache would silently under-block a policy that targets a base
type. The GraphQL side never needs this because its schema was built from the same cache.

**Known gap — family-3 archive stores.** The engine-side stores
(`ITenantContext.GetStreamDataRepository()`, `GetArchiveRuntimeStore()`,
`GetRollupArchiveRuntimeStore()`) open their **own system sessions** internally, so the caller's
`RtSecurityContext` never reaches the archive read and *row-level* data-permission enforcement does
not happen on this path. Threading the security context into those stores is the larger fix and is
deliberately not part of AB#5038. Until it lands, the AB#5030 tenant gate plus this CK-type gate are
the whole barrier — which is exactly the position the GraphQL surface is in, so the two remain
equivalent.

#### Testing

`TestBase` installs a `DefaultHttpContext` (exposed as `TestHttpContext`) behind a registered
`IHttpContextAccessor`, and seeds it with an authenticated user principal —
`tenant_id=test-tenant`, `sub=test-subject`, `role=TestRole` — so family-2/3 tests have a caller by
default. Helpers: `GivenAuthenticatedCaller`, `GivenUnauthenticatedCaller`,
`GivenServicePrincipalCaller`, `GivenCallerWithoutTenantClaim`, `GivenForeignTenantCall`,
`GivenSuccessfulTenantExchange`, `GivenFailingTenantExchange`, `GivenTokenExchange`, plus
`GivenRuntimeCallerToken` / `GivenNoRuntimeCallerToken` for the store on the cross-tenant path.
`BuildPrincipal` emits claims in the shape the bearer handler really produces (`sub` as
`ClaimTypes.NameIdentifier`, `role` as `ClaimTypes.Role`, `tenant_id`/`client_id` unmapped);
`TestJwt.CreateFull(tenantId, subjectId, clientId, roles)` builds the matching JWTs.

**`MockTenantRepository` is cast `.As<ISecureSessionFactory>()`** (`MockSecureSessionFactory`) and
its **parameterless `GetSessionAsync()` throws**. Both matter:
`TenantRepositorySecurityExtensions.GetSessionAsync(ctx)` falls back to the parameterless system
session **silently** for a repository that does not implement `ISecureSessionFactory` — so a mock
without that face makes every call site look correct while enforcing nothing. Verify against
`MockSecureSessionFactory` to assert which `RtSecurityContext` a tool actually opened its session
with. `RuntimeTenantGateTests.SessionOpeningCallSites` is the parametrised proof for all 13
session-opening sites (8 CRUD tools, 3 aggregation tools over 2 sites, `execute_stream_data_query`,
`KnowledgeResources`); `update_entity`'s second post-commit `readSession` needs its own arrangement
and has a dedicated test.
