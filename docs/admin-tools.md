---
description: Adapter pool queue, blueprint catalog refresh, scratch tenants with AI toggles and delete_tenant disableFeatures, role in API secret write errors.
applies_to: **/Tools/{DataFlowTriggerDeploymentSite,TenantManagement,AiLifecycle,ApiSecret,Blueprint}Tools.cs, **/Services/{IdentityForbiddenMessage,AiClientContext}.cs
---

# Administration tools

## Adapter pool queue tools

`get_adapter_pool_queue` and `cancel_queued_execution` (in `DataFlowTriggerDeploymentSiteTools`) are the MCP
third of the queue surface the leasing concept asks for — the same view Refinery Studio and
`octo-cli` show, all three read one endpoint (`GET {tenantId}/v1/adapterPool/{id}/queue`); the cancel runs the CK method
`AdapterPool.CancelQueuedExecution-1` (role `CommunicationManagement`, see `ck-method-clients.md`). Three properties are design
decisions, not implementation detail, and a future "improvement" is most likely to undo exactly
these:

- **Never summarise position into one number.** The pool serves borrowing tenants round-robin, so an
  entry carries `positionInTenant` *and* `tenantsAheadInRotation`; item 1 of the tenant whose turn is
  next runs before item 2 of the tenant being served now. The tool description says so explicitly,
  because the AI reading it is precisely the consumer that would otherwise compute a helpful,
  wrong rank. `AdapterPoolQueueToolsTests` pins that no response property is rank-shaped.
- **`AlreadyLeased` (declared error `EXECUTION_ALREADY_LEASED`, REST 409) and `NotFound`
  (`QUEUED_EXECUTION_NOT_FOUND`, REST 404) are outcomes of a call that worked**, so `IsSuccess`
  stays `true` and the distinction travels in `Outcome`/`WasCancelled`. Reporting the 409 as a
  failure invites a retry, and retrying is not what the caller should do — interrupting the running
  pipeline is a **different operation** on a different path.
- **`cancel_queued_execution` is `High`, not `Medium`.** The risk taxonomy of `tool-conventions.md` would read one queue
  entry as a "single-instance delete", but the work item is destroyed rather than archived and every
  other destructive verb in this family pauses the worker. An AI discarding a tenant's queued nightly
  run without the user seeing the proposal is the case the approval gate exists for. It also requires
  `confirm=true`, as every destructive tool does.

An empty queue is an idle pool and answers `IsSuccess = true`. Manual (non-pooled) adapters have no
queue at all and therefore no tool — that asymmetry is intended.

## Scratch tenants, AI toggles and `delete_tenant disableFeatures`

An agent can work in and clean up the scratch tenants it creates without a human login (`octo-identity-services/docs/tenant-provisioning.md`, "Scratch tenants").

- `create_tenant` takes `scratch` (SDK `CreateTenantAsync(.., scratch: true)`): the client-credentials client of the parent that makes the call becomes
  administrator (`TenantOwners`) of the new tenant. The asset repository answers 403 for a user session or another tenant's token - a plain user
  MCP session cannot create a scratch tenant.  `restore_tenant` has no scratch flag: restore into a tenant created with `create_tenant scratch=true` using the child token - the scratch record survives the restore.
- `enable_ai` / `disable_ai` (`AiLifecycleTools`, High) close the gap of the fourth tenant capability: a tenant with AI Services enabled used to need
  octo-cli `DisableAi` before `delete_tenant`. They use the new `OctoServiceUrls:AiServiceUrl` (default `https://localhost:5019/`; **deployments must set
  `OctoServiceUrls__AiServiceUrl`** or the AI tools fail with the missing-configuration message) and `IOctoServiceClientFactory.CreateAiClient`.
- `get_ai_spend` (Low) / `set_ai_spend_cap` (High, `confirm=true`, reason required) (`AiSpendTools`, AB#8690) read and set the monthly LLM
  spend cap of the central-key LLM gateway, like octo-cli `GetAiSpend` / `SetAiSpendCap`. `get_ai_spend` without `targetTenantId` reads the
  resolved tenant itself; with it, and for `set_ai_spend_cap`, the session must be of the system tenant with the role `AiBudgetAdmin` - the AI
  service refuses everybody else (403), the tool adds no permission of its own. Every change is audited by the AI service. Without `keySource`
  the tenant keeps its stored key source (`CentralKey` when it has none yet).
- `delete_tenant` takes `disableFeatures` (requires `confirm=true`): reads the child's capability status, switches off every enabled one - AI Services,
  Communication, Reporting, Stream Data - through the SDK `TenantFeatureDisabler`, then deletes. All of it with **the session's token for the child tenant**
  (`McpSessionContext.ResolveAccessTokenAsync(server, child)`: the home token when the session is the child's, else an RFC 8693 exchange): the capability
  endpoints belong to the child's own administrator and refuse the parent's token. The response lists `disabledFeatures`; a capability that stays enabled
  (Stream Data with activated archives answers 409 naming them) lands in `featuresStillEnabled` and the tenant is **not** deleted - archives are reported,
  never deleted. A client-credentials session cannot exchange its token for a child (the exchange needs a user); it gets a clear error and uses the child's
  own session (a client-credentials token for the child) or octo-cli `Delete --disable-features`.
- Tests: `ScratchTenantToolsTests` (create flag, disableFeatures order, archives, no child token, enable/disable AI).

## Starter tenant: `create_tenant starter=true`

`create_tenant starter=true` (`StarterTenantSequence`) leaves a new child tenant app-ready in one call:

1. `create_tenant` - create the child. An existing child is resumed (`skipped`) only when an earlier starter call can have left it so:
   the caller already reaches it (the tool never provisions the caller into a tenant it did not just create), and either `System.Ai.Starter`
   is installed and no site is parked (Communication on, or no deployment site yet), or the tenant has no blueprint installation and no
   deployment site at all. Every other existing tenant, also one parked with `disable_communication` (sites kept, Communication off), fails
   here with "exists" and nothing is changed. Any read error refuses too. Installations recorded only as a dependency (e.g. System.Ai.Builder
   of a failed starter install) count as none. Exception: when a first call created the tenant but failed at `provision_admin`, the caller
   has no access yet - run `provision_current_user_as_admin` for the tenant, then call again (the refusal says so);
2. `provision_admin` - provision the calling user as the child's administrator, unless the session's token exchange for the child already works
   (identity sets a new tenant up asynchronously, so the provisioning and the exchange are retried);
3. `install_blueprint` - the newest `System.Ai.Starter` of the tenant's blueprint catalogs (or `starterBlueprintId`), which brings
   `System.Ai.Builder` (the knowledge pages and, from Starter 1.1 / Builder 1.2, the builder agent) as a dependency; skipped when a starter
   is already installed. It runs **before** any enable (AB#8452): the platform then enables Communication and AI Services for the blueprint
   itself (AB#9578), and only in that case does the communication controller deploy the default site and Mesh Adapter
   (`StarterDeploymentService`);
4. `enable_communication` - the tool reads the tenant's features until the platform enabled Communication and
   AI Services (up to 30 reads, 3 s apart). `done` "enabled by the platform"; when the platform did not enable it in time the tool enables it
   itself (`done`, "fallback"); `skipped` when it was enabled before the install;
5. `deploy_defaults` (AB#8452) - makes sure the default site and adapter are deployed, also on the fallback path where the controller deploys
   nothing. Invariant: only the shape of a fresh tenant by the controller's rule (exactly one Cloud site hosting exactly one workload, an
   adapter; read from the tenant repository with the caller's rights, `StarterDefaults`), only what is `Undeployed`, never an
   undeploy, and never a tenant whose Communication was enabled before this call (`skipped`, its sites are left as they are). On the platform
   path it first waits for the controller (up to 20 reads, 3 s apart) and deploys only what is still `Undeployed` afterwards; on the fallback
   path it deploys at once (`RtDeploymentSite.Deploy`, then `RtAdapter.Deploy`). The adapter comes Online once the operator rolled it out;
6. `enable_ai` - enabled by the platform, or by this call when the platform did not in time (fallback); `skipped` when it was enabled before;
7. `default_spend_cap` (AB#9572) - only when the parent is the system tenant (the platform operator's starter path): asks the AI service
   (`IAiServicesClient.ApplyStarterDefaultSpendCapAsync`, `POST system/v1/aiSpendCaps/{tenantId}/starterDefault`, caller's token for the
   system tenant, role `TenantManagement`) for the cluster's default monthly LLM cap on the central key. The request has no amount; the AI
   service takes it from `StarterSpendCap:DefaultTenantSpendCapEur` and never changes an existing entry. `done` = created, `skipped` = the
   tenant had an entry (`exists`), no default is configured (`notConfigured`), the parent is not the system tenant, or the AI service refused
   the caller (401/403); in all skipped cases without a new entry the tenant stays at key source None and the LLM gateway stays closed for it.
   Other errors fail the step;
8. `verify_ready` (AB#9573) - reads, with the caller's own rights in the child (the read path of `knowledge_search`), the ready state: Agent
   `builder-agent` enabled, Mandate `builder-mandate` with write mode Propose, Client `builder-agent-client` (the agent's service account) and
   the knowledge page `app-index`; then the default site and adapter: it fails unless both are `Pending` or `Deployed` (Undeployed, Error,
   Disabled or an unreadable state: no pipeline would run) and names how to deploy them by hand. Its detail names the adapter state and the LLM cap of step 7. It changes nothing and is never
   skipped; a starter without the builder agent (System.Ai.Starter 1.0.0) installs but fails here, naming what is missing.

When no step failed but step 7 left the tenant without a cap, the message says the tenant is set up but **not app-ready for LLM use** (LLM
gateway closed until an `AiBudgetAdmin` sets a cap); "app-ready" is only claimed with a cap entry. The waits (steps 2, 4, 5) take the MCP
request's cancellation token, so a client that gives up stops the sequence.

`starterSteps` lists every step with `done`, `skipped`, `failed` or `notRun`; on a failure `failedStep` names the step and the steps after it are
`notRun`. Calling the tool again with the same `childTenantId` completes the remaining steps and skips the finished ones. Known limit: when
`deploy_defaults` itself failed, a repeated call sees Communication as enabled before the call and leaves the site and adapter alone; its
`verify_ready` then fails and says so. Deploy them by hand (octo-cli `DeployDeploymentSite`, then `DeployWorkload`; MCP `deploy_workload`
for the adapter). `starterBlueprintId` must name a `System.Ai.Starter` blueprint; another id is refused before anything is created.

The steps in the child run with the caller's token for the child (RFC 8693 exchange), and identity issues that only for a user. So `starter=true`
is **refused before anything is created** for a client-credentials caller and together with `scratch=true`; no half-provisioned tenant is left.
Tests: `StarterTenantToolsTests`, `StarterReadyCheckTests`, `StarterDefaultsTests`.

## Blueprint catalog refresh

`refresh_blueprint_catalogs` (`BlueprintTools`, Medium) mirrors octo-cli `RefreshBlueprintCatalogs`: SDK
`IAssetServicesClient.RefreshBlueprintCatalogsAsync(catalogName)` (`POST system/v1/blueprints/catalogs[/{name}]/refresh`,
always forced, `DataModelManagementPolicy`). A blank name means all catalogs (same rule as SDK and CLI); a name with other characters than ASCII letters, digits,
`.`, `-`, `_` (or `.`/`..`) is refused before the call, because the SDK puts it into the path unescaped. The per-catalog
`Results` go on the wire as-is; any `Failed` entry makes `IsSuccess = false` (the CLI exits non-zero) while `Results` and
the counts stay set. A 404 for a named catalog becomes `ErrorCode = CatalogNotFound` (`BlueprintTools.CatalogNotFoundCode`);
everything else goes through `ServiceProblemMessage` (403 becomes `DataModelManagementRequired`). The refresh reaches only the
asset-repository replica that answers; the tool description says so - drop that sentence once the asset repository
refreshes every replica.
Tests: `BlueprintToolsTests` (refresh section), `DataModelManagementRequiredTests`.

## API secret writes: 403 names the missing role

The write tools of `ApiSecretTools` (create/update/delete for client and API resource) return `IdentityForbiddenMessage.Describe(ex)` as
`errorMessage`: for a `403` the `message` of the identity body verbatim (`{"message": "...requires the role 'SecretManagement'..."}`;
the SDK keeps the body in the exception text), for a 403 without body or `message` a generic access-denied text that names
`SecretManagement`, for every other error the exception text. The tool descriptions state that writes need the tenant role `SecretManagement`
(`TenantManagement`/`UserManagement` only while the cluster runs `SecretManagementRoleEnforcement=Warn`). Tests: `ApiSecretToolsTests`.
