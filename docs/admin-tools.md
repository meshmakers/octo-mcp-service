## Adapter pool queue tools (AB#4924 §10)

`get_adapter_pool_queue` and `cancel_queued_execution` (in `DataFlowTriggerPoolTools`) are the MCP
third of the queue surface the leasing concept asks for — the same view Refinery Studio and
`octo-cli` show, all three off one endpoint
(`GET`/`DELETE {tenantId}/v1/adapterPool/{id}/queue[/{executionId}]`). Three properties are design
decisions, not implementation detail, and a future "improvement" is most likely to undo exactly
these:

- **Never summarise position into one number.** The pool serves borrowing tenants round-robin, so an
  entry carries `positionInTenant` *and* `tenantsAheadInRotation`; item 1 of the tenant whose turn is
  next runs before item 2 of the tenant being served now. The tool description says so explicitly,
  because the AI reading it is precisely the consumer that would otherwise compute a helpful,
  wrong rank. `AdapterPoolQueueToolsTests` pins that no response property is rank-shaped.
- **`AlreadyLeased` (409) and `NotFound` (404) are outcomes of a call that worked**, so `IsSuccess`
  stays `true` and the distinction travels in `Outcome`/`WasCancelled`. Reporting the 409 as a
  failure invites a retry, and retrying is not what the caller should do — interrupting the running
  pipeline is a **different operation** on a different path.
- **`cancel_queued_execution` is `High`, not `Medium`.** The taxonomy above would read one queue
  entry as a "single-instance delete", but the work item is destroyed rather than archived and every
  other destructive verb in this family pauses the worker. An AI discarding a tenant's queued nightly
  run without the user seeing the proposal is the case the approval gate exists for. It also requires
  `confirm=true`, as every destructive tool does.

An empty queue is an idle pool and answers `IsSuccess = true`. Manual (non-pooled) adapters have no
queue at all and therefore no tool — that asymmetry is intended.

## Scratch tenants, AI toggles and `delete_tenant disableFeatures` (AB#7192)

An agent can work in and clean up the scratch tenants it creates without a human login (octo-identity-services `CLAUDE.md`, "Scratch tenants").

- `create_tenant` takes `scratch` (SDK `CreateTenantAsync(.., scratch: true)`): the client-credentials client of the parent that makes the call becomes
  administrator (`TenantOwners`) of the new tenant. The asset repository answers 403 for a user session or another tenant's token - a plain user
  MCP session cannot create a scratch tenant.  `restore_tenant` has no scratch flag: restore into a tenant created with `create_tenant scratch=true` using the child token - the scratch record survives the restore.
- `enable_ai` / `disable_ai` (`AiLifecycleTools`, High) close the gap of the fourth tenant capability: a tenant with AI Services enabled used to need
  octo-cli `DisableAi` before `delete_tenant`. They use the new `OctoServiceUrls:AiServiceUrl` (default `https://localhost:5019/`; **deployments must set
  `OctoServiceUrls__AiServiceUrl`** or the AI tools fail with the missing-configuration message) and `IOctoServiceClientFactory.CreateAiClient`.
- `delete_tenant` takes `disableFeatures` (requires `confirm=true`): reads the child's capability status, switches off every enabled one - AI Services,
  Communication, Reporting, Stream Data - through the SDK `TenantFeatureDisabler`, then deletes. All of it with **the session's token for the child tenant**
  (`McpSessionContext.ResolveAccessTokenAsync(server, child)`: the home token when the session is the child's, else an RFC 8693 exchange): the capability
  endpoints belong to the child's own administrator and refuse the parent's token. The response lists `disabledFeatures`; a capability that stays enabled
  (Stream Data with activated archives answers 409 naming them) lands in `featuresStillEnabled` and the tenant is **not** deleted - archives are reported,
  never deleted. A client-credentials session cannot exchange its token for a child (the exchange needs a user); it gets a clear error and uses the child's
  own session (a client-credentials token for the child) or octo-cli `Delete --disable-features`.
- Tests: `ScratchTenantToolsTests` (create flag, disableFeatures order, archives, no child token, enable/disable AI).

## API secret writes: 403 names the missing role (AB#8094)

The write tools of `ApiSecretTools` (create/update/delete for client and API resource) return `IdentityForbiddenMessage.Describe(ex)` as
`errorMessage`: for a `403` the `message` of the identity body verbatim (`{"message": "...requires the role 'SecretManagement'..."}`, AB#5861;
the SDK keeps the body in the exception text), for a 403 without body or `message` a generic access-denied text that names
`SecretManagement`, for every other error the exception text. The tool descriptions state that writes need the tenant role `SecretManagement`
(`TenantManagement`/`UserManagement` only while the cluster runs `SecretManagementRoleEnforcement=Warn`). Tests: `ApiSecretToolsTests`.
