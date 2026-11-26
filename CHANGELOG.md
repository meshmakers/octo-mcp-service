# Changelog

## Unreleased

### Added

- **`get_ai_spend` and `set_ai_spend_cap` (AB#8690, new file `Tools/AiSpendTools.cs`).** Read (risk Low) and set (risk High,
  `confirm=true`, reason) the monthly LLM spend cap of the central-key LLM gateway through the AI service
  (`{tenant}/v1/aiSpend`, `system/v1/aiSpendCaps`). Setting and reading another tenant need a system-tenant session with the
  role `AiBudgetAdmin`; the AI service enforces it. Without `keySource` the tenant keeps its stored key source (`CentralKey` when it has none yet); `targetTenantId` must be one route segment (no `.`, `..`, `/`). Equivalent to octo-cli `GetAiSpend` / `SetAiSpendCap`.
- **Agent tokens reach the platform only through MCP (AB#9729 option A, Feature AB#6054, security).** `create_agent`
  gives the agent client the scope `octo_agent` instead of `octo_api` (System.Identity.Bootstrap 1.11.0), so its token
  opens no platform API. The transport admits `octo_agent` only on client tokens; `AgentMandateGuard` refuses it for a
  caller that is no agent of the token's tenant (`NotAnAgent`). For every downstream call
  `McpSessionContext.ResolveAccessTokenAsync` exchanges the agent token for a five-minute `octo_api` token with `act` =
  the agent client id, through the new confidential client `octo-mcpServices-agent-exchange` (registered in every tenant
  with `Mcp:AgentExchangeClientSecret`, fingerprint for rotation; identity data version 4). No secret means no client,
  no exchange and every downstream call of an agent refused. An agent never acts in another tenant. Needs
  octo-identity-services with the agent exchange (same Feature). Details: `docs/agent-tools.md`.
- **Static mandate check of agent tool calls (AB#9729, Feature AB#6054, security).** A `tools/call` filter
  (`AgentMandateFilter`) refuses a call of an agent's client (its `ServiceAccountClientId`, token tenant only) that its
  mandate does not cover: `AgentDisabled`, `ToolNotGranted`, `RiskAboveCeiling` (grant `MaxRisk`, absent = Low, or the
  mandate's ceiling) and `MandateUnavailable` (fail closed: lookup error, ambiguous agent or mandate, missing tenant). The
  tool does not run; other callers are not affected; no cache. The writing agent tools refuse every agent caller
  (`AgentCaller`, replaces `SelfModification`) and write on the channel `System.Ai/AgentAdministration`, the only way
  System.Ai 4.12.0 admits writes of `Agent` and `Mandate` (generic entity tools and GraphQL are refused by the engine).
  `docs/agent-tools.md`.
- **Agent tools (AB#8454, Feature AB#6054).** `create_agent`, `update_agent`, `set_agent_enabled` (Medium risk),
  `list_agents` and `get_agent` (Low) manage the System.Ai `Agent` and `Mandate` (System.Ai 4.12.0) of a tenant and the
  agent's service-account client in the tenant identity (System.Identity CK methods). Grants must name tools of this
  server (`UnknownTool`), stay at or below the risk ceiling and default to a narrow mandate (no grants, Low, budget 0,
  Propose). The writing tools need a tenant administrator (`TenantManagement` or `UserManagement`, else
  `NotTenantAdministrator`) and refuse agent callers (`AgentCaller`, AB#9729). Entities are written with the
  caller's session before the client is created; a failed identity step removes them again. The client secret is never returned. `docs/agent-tools.md`.

### Changed

- **No routed `ExecutePipelineRequest` command client (AB#7465).** The service no longer registers it: only the
  communication controller publishes that request, and `execute_pipeline` runs the controller's `Pipeline.Execute`
  method. An architecture test keeps the registration out. No behaviour change.
- **Dependency pass (AB#9613, Feature AB#9579).** Third-party NuGet packages raised to their newest version on the .NET 10 line: ModelContextProtocol.AspNetCore 2.4.0, NLog.Web.AspNetCore 6.3.0, OpenTelemetry.Exporter.InMemory 1.21.0 (test), Moq 4.21.0, FluentAssertions 8.11.0, xunit.v3 4.0.3, xunit.runner.visualstudio 4.0.1, Microsoft.NET.Test.Sdk 18.12.0, coverlet.collector 10.1.1. Runtime-aligned packages stay on 10.0.15; the 11.0.x line moves with AB#9580. No behaviour change.
- **Communication wave 3: the communication write tools run a CK method of System.Communication.** `deploy_pipeline`,
  `execute_pipeline`, `dry_run_pipeline`, `set_pipeline_debug`, `move_pipelines`, `deploy_data_flow`, `undeploy_data_flow`,
  `deploy_triggers`, `undeploy_triggers`, `undeploy_deployment_site`, `deploy_workload`, `undeploy_workload`,
  `update_workload_chart_version` and `cancel_queued_execution` call the generated method clients
  (`construction-kits.yaml`: `System.Communication-[4.19,5.0)`) instead of the REST methods of
  `ICommunicationServicesClient`; the reads stay on REST. Tool names, parameters and response fields are unchanged.
  - **Requires communication controller services with System.Communication 4.19; roll them out before this MCP version.**
    There is no REST fallback: a method the controller does not offer is reported as `IsSuccess=false` with the needed
    version; nothing is changed.
  - **The tools need the role `CommunicationManagement`** (the REST endpoints accepted every `octo_api` caller); a caller
    without it gets `METHOD_FORBIDDEN`.
  - Refusals are reported as `CODE: message (params)`; a malformed runtime id is refused before any call. A workload is
    addressed through the client of its concrete type (`ckTypeId` read first, element version ignored).
- **Identity wave 2: every identity write tool runs a CK method of System.Identity.** Groups, roles, clients (create,
  update, delete, roles, groups, overlays, mirrors), API scopes, API resources, client and API resource secrets, identity
  providers, e-mail domain group rules, external tenant user mappings and admin provisioning call the generated method
  clients (`construction-kits.yaml`: `System.Identity-[3.8,4.0)`) instead of the REST methods of `IIdentityServicesClient`;
  the reads stay on REST. Tool names, parameters and response fields are unchanged.
  - **Requires octo-identity-services with the wave 2 methods (System.Identity 3.8); roll out identity before this MCP
    version.** There is no REST fallback: a method the identity service does not offer is reported as
    `IsSuccess=false` with the needed identity version (and, for the secret-carrying tools, the bus switch
    `Methods:Identity:EnablePasswordMethods`); nothing is changed.
  - Refusals are reported as `CODE: message (params)` (`METHOD_FORBIDDEN`, `METHOD_VALIDATION_FAILED`, declared errors such
    as `GROUP_CYCLE`); `create_external_tenant_user_mapping` keeps its `errorCode` values. Declared errors the REST
    endpoints treated as done (member already in / not in the group, child group already in / not in the group, removing a
    role a client does not have) stay a success.
  - Secrets: the secret generated by `create_client_secret` / `create_api_resource_secret` appears only in
    `clearTextSecret`; `secretValue` is the stored value as before. `clientSecret` and `valueEncrypted` are sent in the
    method call and are redacted from every error text; the update and delete secret tools still echo the given
    `valueEncrypted` in `secretValue`, as before.
  - **Breaking: `create_client_secret` and `create_api_resource_secret` require `confirm=true`** (decided for this
    change: a tool whose result carries a secret asks for confirmation, as `reset_user_password`). A call without it is
    refused before anything is created.

### Security

- **AB#8454** — A failed `create_agent` keeps nothing: when any step after the entity write fails or throws (identity
  error, transport error of `SetRoles`, failed read-back) the created client, agent and mandate are removed again.
  `enabled: false` now also creates the service-account client disabled (before, the client was always enabled).
- **AB#6988** — The file-transfer endpoints are no longer anonymous and the transfer id is out of the URL. **Breaking for
  HTTP clients of `/file-transfer/*`.**
  - `PUT /file-transfer/upload` and `GET /file-transfer/download` require the creator's bearer token (same policy as
    the MCP transport: no or invalid token 401, missing platform API scope 403) or the one-time transfer key below.
  - The id travels in the request header `X-Transfer-Id`; the routes `/file-transfer/{upload,download}/{id}` are removed
    (404) and the query is not read, so access logs and span attributes (`url.*`, `http.route`) no longer carry it.
    Error responses and log lines do not echo it.
  - An entry is served only to the principal that created it (`McpCallerPrincipal.TransferOwnerKey`: same user and
    tenant, or same client id and tenant, so a mirrored client of another tenant is refused); anyone else gets 404, like an unknown id. An entry or caller without a principal matches
    nobody. The tool side keeps its session binding and checks the same owner key, so a mirrored client of another
    tenant cannot consume or cancel the upload through a tool either.
  - The first PUT claims the upload slot atomically: a concurrent second PUT gets 409; a failed or aborted PUT ends the
    reservation and deletes the partial file (a new `prepare_file_upload` is needed).
  - `prepare_file_upload` and every download-producing tool (`dump_tenant`, `export_archive_data`,
    `export_runtime_model_by_query`, `export_runtime_model_by_deep_graph`, `export_runtime_graphql_sdl`, `download_file`)
    return the path without the id plus the new field `transferIdHeader`; their messages name both headers.
  - **One-time transfer key** for clients without the raw access token of their MCP connection: `prepare_file_upload`
    and every download-producing tool also return `transferKey`, `transferKeyHeader` (`X-Transfer-Key`) and
    `transferKeyExpiresAtUtc`. The endpoints accept the creator's bearer token **or** the key. The key is 256 random
    bits, stored only as a hash, bound to the transfer, its direction, the creator's principal and tenant, valid
    `Mcp:FileTransferKeyLifetime` (default 5 minutes, at most 10, checked at start) and spent atomically on first use.
    A missing, wrong, expired, used, foreign or wrong-direction key, or a key presented with the token of another
    principal, gets 401 with one identical body.
  - A client-credentials token without `tenant_id` can no longer own a transfer: the tools refuse to create one and the
    endpoints refuse it (fail closed).
  - Fixed: under load an aborted download could be deleted as if it had been delivered (Kestrel discards writes after
    an abort and cancels `RequestAborted` late); the delivery is now also checked on the response pipe's flush result.
  - Known limitation: no per-caller disk quota yet.

### Added

- **`import_ck_model` accepts the draft inline, the same way `validate_ck_model` does.** Pass exactly one of `transferId`
  (upload, unchanged) or `files` (name/content pairs relative to `ConstructionKit/`, the source folder or one compiled model;
  1 MiB / 2000 files, over the limit `InlineDraftTooLarge` / message 9005). An MCP-only client without an HTTP client can now
  validate and import a draft without the upload PUT. A source draft is compiled by the asset repository's `ImportCk` as an
  uploaded source zip is; on `CK_MODEL_COMPILE_FAILED` the response now carries the findings in `errors`/`warnings`
  (`messageNumber`, `level`, `file`, `elementPath`, `message`), also for an uploaded source zip, and nothing is queued. Risk
  level stays Medium; the upload remains the path for larger drafts. The response type is `CkModelImportResponse`
  (`JobStartedResponse` plus `errors`/`warnings`).

- **New tool `invoke_ck_method(ckTypeId, methodId, rtId?, parameters?, idempotencyKey?, tenantId?)`**: calls one method of a
  CK type (an app method such as a booking) through the tenant's GraphQL endpoint with the caller's own token, so the
  method gateway of the asset repository (method roles, `allowSelf`, scopes, target, parameter validation) decides exactly
  as for a GraphQL caller. A refused call returns `methodError` with the same `code`, `parameters` and `details` a GraphQL
  caller reads from `errors[].extensions` (e.g. a declared `CONCURRENT_CHANGE`, or `METHOD_FORBIDDEN`), plus `declared`.
  Risk **Medium** (write), like the generic CRUD write tools. Refused before any call: methods of System models (they have
  dedicated tools), knowledge page types, methods with a sensitive result or parameter, an abstract or unknown type or
  method, malformed or oversized (256 KiB) parameters, a malformed `rtId` or idempotency key. Known limitation: no MCP
  tool reads CK operation results yet (asynchronous methods return the operation handle). See
  `docs/ck-method-clients.md`.

- **AB#9182** — New tool `refresh_blueprint_catalogs(catalogName?)`, the MCP mirror of octo-cli `RefreshBlueprintCatalogs`:
  forces a refresh of all blueprint catalog caches of the asset repository, or of one catalog, and returns the result per
  catalog (`Refreshed`, `Skipped`, `Failed` with message) plus the three counts. A failed catalog does not stop the others
  but makes the call report `isSuccess: false`; an unknown catalog name answers `errorCode: CatalogNotFound`, a name with characters other than ASCII letters, digits,
  `.`, `-`, `_` is refused before the call (the SDK puts it into the path unescaped); without the
  tenant role `DataModelManagement` the call fails with `DataModelManagementRequired`. Risk Medium (like
  `refresh_ck_catalogs`). The description names what a refresh does not fix: the per-replica cache (bug AB#9169, a
  second refresh may be needed on multi-replica installations) and the CDN delay of up to 10 minutes after a GitHub Pages
  deploy. Agents no longer need a pod restart to see a freshly published blueprint.
- **AB#9188** — The AI service's MCP service client is trusted for cross-tenant service calls by default. New option
  `Mcp:AiServiceClientId` (`OCTO_MCP__AISERVICECLIENTID`, default `octo-ai-adapter`); `ConfigureTenantAuthorizationOptions`
  appends it to `TenantAuthorization:CrossTenantServiceClientIds` after the section is bound (operator entries stay, no
  duplicate, a `*` pattern is refused at startup, empty switches it off). The AI service's system-tenant token now
  reaches `/{tenantId}/mcp` of every AI tenant without a per-cluster value; any other client's foreign-tenant token is
  still refused with 403. `AiServiceClientTenantGuard` binds the entry to the system tenant: a client of the same id
  created in another tenant gets 403 on foreign tenant routes (client ids are unique per tenant only).
- **AB#9148** — Knowledge clients may rely on the descriptions of this server's own tools. `answeringRules` gains a
  third rule (`KnowledgeConventions.ToolRule`): the description of one of this server's tools is a source when the
  answer recommends or uses that tool, cited as `[tool:<name>]` (e.g. `[tool:add_client_to_role]`). Page citations still
  need `knowledge_get`; the sentence `No knowledge page answers this question.` is unchanged and applies only when
  neither a page nor a tool answers (then nothing is cited). The server instructions, the prompt
  `octomesh-knowledge-first`, the built-in default conventions, the no-match message and the `knowledge_search`
  description say the same.
- **AB#9088** — Stricter answering rules for knowledge clients. `knowledge_search` results carry the new field
  `answeringRules` (a code constant, not tenant-writable, applies whatever the schema page's conventions say): cite only
  pages opened with `knowledge_get` for this answer, and when no opened page answers reply with exactly
  `No knowledge page answers this question.` and cite no page. The server instructions, the prompt
  `octomesh-knowledge-first`, the built-in default conventions and the no-match message carry the same rules. Existing
  fields are unchanged.
- **AB#9087** — Knowledge first. The MCP `initialize` result carries `instructions` (a code constant, not tenant-writable):
  for questions on how to build, administer, deploy or diagnose on OctoMesh call `knowledge_search` first, read pages with
  `knowledge_get`, cite `[slug]`, follow the returned answering conventions; it lists the area index pages `admin-index`,
  `app-index`, `pipeline-index`, `deploy-index` and `diagnose-index`. New MCP prompt `octomesh-knowledge-first` (argument
  `question`) wraps a question with these steps. `knowledge_search` gets the new field `builderKnowledgeHint` when the
  tenant has no Skill page (blueprint `System.Ai.Builder` not installed; opt-in via `InstallBlueprint -b
  System.Ai.Builder-<version>` or `install_blueprint`); existing fields are unchanged. When the listed or matched pages
  hold no Skill page, one query for a single page of kind Skill decides (independent of the tenant's page count). Server options and capabilities are
  registered through `McpServerSetup` (host and protocol tests).

### Fixed

- **AB#9189 (security, P1)** — A client-credentials token of one tenant can no longer read or write another tenant's
  runtime data through the direct-engine tools. The in-tool tenant gate (`RuntimeSecurityContextResolver`) exempted every
  service token (no `sub`) and acted for whatever tenant the tool parameter `tenantId` named, on `/{tenantId}/mcp` (the
  parameter wins over the route) and on the tenantless `/mcp`. A service token now needs the exact tenant match
  (`tenant_id` = target tenant) like a user token; a token without `tenant_id` is denied. The only cross-tenant pair is
  the AI service client (new option `Mcp:AiServiceClientId`, `OCTO_MCP__AISERVICECLIENTID`, default `octo-ai-adapter`,
  exact id) with a token of the system tenant (`System:SystemTenantId`); the same id from any other tenant is denied.
  Every service-token decision is logged (client id, token tenant, target tenant, outcome, reason; denials at Warning)
  and counted in `octo.tenant_auth.mcp_service_token_decisions{outcome,reason}`. Tenant-bound callers (AI worker,
  mesh-adapter node, per-tenant agent clients) are unaffected.

- **AB#9165** — `knowledge_search` ranked broad overview pages above the specific page and gave no sign when the pages
  missed what a question was about (M1 live eval 1.1.2 on m1-e2e: "build an HTTP API for my app" listed
  `app-http-api-pipelines` 13th; q20 "AWS Lambda" cited `deploy-index` and `deploy-promotion`).
  Ranking: function and filler words (for, how, the, ago, something, number words, ...) are no longer query words
  when the query has others, the slug counts as a field (weight 3), every query word the title or slug names adds a
  bonus of 15, and the stemmer reduces the plural of three-letter acronyms (APIs -> api). Each listed page now carries
  `matchedTerms`; the response carries `unmatchedTerms`, the query words no searchable page contains (any kind or
  status, Rejected only when asked for). When one of them is a content word, the message says the pages match only
  the other words and, if the question is about the missing ones, to reply with exactly
  `No knowledge page answers this question.` - unless a page or one of this server's tools answers it; a part they
  answer stays answered, a name of the user's own app or tenant is no reason by itself. Stop words, manner adverbs
  ("instantly") and typos (6+ letters, one edit away from a page word) do not count, and each counted word is searched
  on its own first, so a page outside the 200-page preselection that contains it removes it. `answeringRules` is
  unchanged.
  Offline evidence against the eval tenant's pages (builder blueprint + eval corpus) and two forms of every eval
  question - the queries the model sent in the live eval 1.1.2 and the whole question text (fixture in the tests):
  an expected page in the top 3 for 38 of 38 answerable forms (was 34), top 1 for 31 (was 21); 0 no-answer hints on
  the 38 answerable forms; the hint on all 5 no-answer question texts and on 8 of 9 recorded no-answer queries (not on
  "Refinery Studio Wi-Fi network", q23, whose words are all on some page).

- **AB#9114** — `knowledge_adopt_platform_updates` (and the `knowledge-platform-updates` pipeline that calls it) adopted
  only one page per run: every further page was skipped with `Failed: Session already started.`, because all pages
  shared one session and a session carries exactly one transaction. Each page is now adopted in its own session and
  transaction, so one run switches every unedited page. A run that fails part-way (a session cannot be opened) answers
  with the error and still lists the pages it already switched.

- **AB#9103** — An edited knowledge page lost its platform update silently: the tenant's edit took the key `<slug>@2`,
  the blueprint update shipped its own `<slug>@2`, and UpdateBlueprint kept the tenant entity under the shared key
  (found by the local M1 verification, System.Ai.Builder 1.0.0 -> 1.1.0). Versions written by the knowledge tools are
  now keyed `<slug>@t<n>` (decision Odin 2026-11-16); `<slug>@<n>` belongs to the blueprint. `VersionNumber` stays one
  sequence; version lookups by number read both keys (`KnowledgePages.VersionNames`, `FindVersionAsync`); a shared
  number names the Blueprint version for `knowledge_get` / `knowledge_restore_version` and in `RestoredFromVersion`
  chains, the tenant version for the current version and `BaseVersion`; `knowledge_get` and `knowledge_restore_version`
  take `tenantVersion: true` to name the tenant one. Existing tenant versions keep their plain key (no migration), so a
  blueprint version with the number of such an old tenant version is still held back - only test tenants have them.

- **AB#9023** — `validate_pipeline_definition` reported `nodeCount: 0` for pipelines without a root `nodes` array. It now
  counts every node: the root `triggers`, `transformations` and legacy `nodes` lists and the node lists nested in control
  nodes (`transformations` of ForEach, If, Group, ...; `cases[].transformations` and `default` of Switch). Arrays inside a
  node's own data are not counted. The rest of the result is unchanged.

- **AB#9015** — `validate_pipeline_definition` leaked the adapter's composite pipeline schema on every call. JsonSchema.Net
  registers each schema it builds in the process-wide `SchemaRegistry.Global`; the composite schema has no `$id`, so every
  build got a new random base URI and stayed there for the life of the pod (measured in a test: 100 calls with a 0.7 MB
  schema retained ~680 MB). The MCP pod on test-2 was OOM-killed at its 512 MiB limit during agent runs. The schema is now
  built into a per-call registry (`PipelineSchemaCompiler`) and is collected after the call; `PipelineSchemaCompilerTests`
  guards it (no global registration, 40 repeated calls retain < 20 MB).

### Changed

- **AB#9088** — `knowledge_search` leaves pages whose current status is Rejected out of results and the index unless the
  `status` filter is `Rejected`; `knowledge_get` still reads them by slug. Cause: three file-back Drafts set to Rejected
  on kb-test2 still ranked #1, #2 and #5.
- **AB#8418** — `create_user` and `reset_user_password` call the CK methods `System.Identity/User.CreateUser-1` /
  `User.ResetPassword-1` through the generated `RtUserMethodClient` instead of the REST endpoints. Names, parameters and the
  response shape are unchanged; `reset_user_password` still needs `confirm=true`. Refusals come from the server as
  `"{Code}: {message} [detail codes]"` — the failed rules of a `PASSWORD_POLICY_VIOLATION` arrive only as detail codes (`USER_ALREADY_EXISTS`, `USER_NAME_RESERVED`, `PASSWORD_POLICY_VIOLATION`,
  `SHADOW_USER_PASSWORD_NOT_ALLOWED`, `METHOD_FORBIDDEN`, `METHOD_TARGET_NOT_FOUND`). No REST fallback: a server without the
  method gets a message naming the needed identity version (3.5.57 / 3.5.52 with `Methods:Identity:EnablePasswordMethods`).
  The password parameters are described as `SENSITIVE`, never echoed (error texts are scrubbed), and the new
  `McpPayloadLogFilter` keeps the SDK's Trace-level JSON-RPC payload logging (tool arguments) out of the service log.
  The error texts of the user tools (`get_users`, `create_user`, `update_user`, `delete_user`, `reset_user_password`,
  `add_user_to_role`, `remove_user_from_role`) also pass the new `ErrorTextRedactor`: a developer exception page a service
  answered with (header section with `Authorization`), bearer tokens and JWTs are removed before the text reaches the client.
  `AddMcpPayloadLogFilter` also adds its rule per logging provider, because the provider rule `UseNLog()` adds for
  `NLogLoggerProvider` would otherwise win over the category rule and let the Trace payloads reach NLog.

- **AB#8904** — the entity query tools page and refuse unbounded loads (Feature AB#8793). They read the tenant database
  directly, so the asset-repo API guard (AB#8898) never sees them. `query_entities`, `query_entities_simple`,
  `navigate_associations`, `get_association_tree` (root entities) and `execute_runtime_query` (`RtSimpleRtQuery`) used to
  load every matching entity when no limit was given. Now: without a page size the engine gets take = 100
  (`DynamicTools:DefaultQueryLimit`); a page size above 1000 (`DynamicTools:MaxQueryResultLimit`) is refused with an error
  that names the limit, before any engine query runs (never silently capped); a page size below 1, a negative skip or a
  skip whose next page would overflow an int is refused too. New arguments: `skip`/`take` on `navigate_associations`,
  `rootSkip`/`rootTake` on `get_association_tree`.
  Every result carries a `paging` block (`skip`, `take`, `defaultTakeApplied`, `maxTake`, `returnedCount`, `hasMore`,
  `nextSkip`, `hint`); the tool descriptions state default, maximum and how to page. `MaxQueryResultLimit` can be lowered
  but not raised above 1000 (`DynamicToolOptions.MaxPageSize`): a higher value is ignored, logged at startup and reported
  by the health check (Degraded). Behaviour change for MCP clients: a call without a limit returns at most 100 items plus
  the paging hint. Unchanged: `query_entities_aggregation` / `query_entities_grouping` (aggregates, no entities; pinned by
  their `PassesNullPagingToEngine` tests) and the children per level of `get_association_tree`. Tests:
  `Tools/RuntimeQueryPagingTests`.

### Added

- **AB#9005** — `validate_ck_model` accepts the CK model draft inline, so an MCP-only client (e.g. a `claude -p` builder
  agent) validates it in one call without `prepare_file_upload` and an HTTP PUT: `files` (name/content pairs relative to
  `ConstructionKit/`), up to 1 MiB of UTF-8 content and 2000 files. Exactly one of `transferId` and `files` is given; the
  result contract (AB#8492) is unchanged. Over the limit the call is refused with `errorCode` `InlineDraftTooLarge` and an
  error with `messageNumber` 9005 that points to the upload path. No base64 zip parameter: binary payloads stay on the
  file-transfer endpoints. Authentication is checked before the draft is staged; the draft becomes an owner-only temporary
  zip that is deleted when the call ends (no process-wide state, AB#9015); file names with `..` or absolute paths and
  duplicates are rejected. Risk stays Low.

- **AB#8780** — platform updates of Blueprint knowledge pages (System.Ai 4.10.0). A blueprint update brings new
  `KnowledgePageVersion` entities but never refreshes an existing page (AB#8778). New tools:
  `knowledge_list_platform_updates` (read), `knowledge_adopt_platform_updates` (medium risk, `runRef` required) and
  `knowledge_dismiss_platform_update` (medium risk). Unedited pages (current version is a Blueprint version: author
  `blueprint:…` and the engine's `rtBlueprintSource` stamp) are switched to the newest Blueprint version without a new
  version: content and status from that version, `CurrentVersion`, `BlueprintVersion`, `AdoptedByRun` = `runRef`,
  `LinksTo` follows the version's `DeclaredLinks` (absent = unchanged). Edited pages are never changed; they are listed as
  `Available` (the lint's `PlatformUpdateAvailable`) until a person restores the version or dismisses it
  (`DismissedBlueprintVersion`). `knowledge_write_lint_report` accepts the kind `PlatformUpdateAvailable` (key 6) on
  tenants with System.Ai 4.10.0. A page whose content differs from its current Blueprint version (direct edit by a
  signed-in user) counts as edited; discovery is paged and batched. Tests in `Tools/KnowledgePlatformUpdateToolsTests`
  and `Tools/KnowledgeLintToolsTests`.

### Fixed

- **AB#8860** — The test build compiles again against an SDK that marks `IIdentityServicesClient.CreateUser(UserDto)` and
  `ResetPassword(string, string)` `[Obsolete]` (octo-sdk #113): the assertions in `UserManagementToolsTests` that check the REST
  methods are *not* called suppress CS0618 locally with `#pragma warning disable/restore`. No product code changed.

- **AB#8780** (reopen, kb-test2 2026-11-12) — an edited page no longer hides a Blueprint version it never saw. The rule
  compared version numbers and arrival times, so an edit written on `@1` after `@2` arrived reported nothing and the
  `@2` content was lost silently. `KnowledgePlatformUpdates.Decide` now walks the ancestor chain of the current version
  (`BaseVersion`, `RestoredFromVersion`, root `@1`); every Blueprint version outside it (and above the dismissed one) is
  reported as `Available`. Page writes record `BaseVersion` on tenants with System.Ai 4.11.0; older versions without it
  end their chain (safe direction: a finding). Unedited pages are adopted as before.
- **AB#8780** — the answering conventions of `knowledge_search` fall back to the newest Verified version of
  `knowledge-schema` the tenant approved or adopted while the page is Draft (an MCP edit not yet verified), instead of
  the built-in default; a Blueprint version counts only in the ancestor chain of the current version (never an unadopted
  or superseded seed), a newer Rejected version still gives the default. Version histories are read page by page; a chain longer than 200 steps marks the scan `Truncated`.
- **AB#8780** — a page write (`knowledge_upsert_page`, `knowledge_restore_version`, `knowledge_set_status`) takes the next
  free version number of the page instead of `CurrentVersion + 1`. A blueprint update brings versions above the current
  one (`<slug>@<n>`), and the versions of a deleted page stay; the old rule wrote a second version with the same name.

### Security

- **AB#8575** — the knowledge write tools (`knowledge_upsert_page`, `knowledge_link`, `knowledge_set_status`,
  `knowledge_restore_version`) open their caller session on the trusted write channel `System.Ai/KnowledgeTools`
  (`KnowledgePages.WriteChannel`, `RtSecurityContext.WithWriteChannel`). System.Ai 4.8.0 restricts writes of
  `KnowledgePage` and `KnowledgePageVersion` to signed-in users and this channel (`writeAccess`, engine AB#8574), so the
  tools keep writing for every caller (content guard, versioning, no Verified), while a client-credentials token that
  writes those types directly via GraphQL, REST or ImportRt is refused by the engine with message 144. The read tools
  get no channel; the session still acts for the caller (roles, data permissions, author). Tests in
  `Tools/KnowledgeToolsTests` (`WriteTools_OpenTheirSessionOnTheKnowledgeToolsChannel_ForTheCallerItself`,
  `ReadTools_NeverOpenTheirSessionOnTheChannel`, `WriteChannel_IsTheChannelOfTheSystemAiWriteAccessBlock`).

### Fixed

- **AB#8580** — the `VerifiedNotAllowedOverMcp` refusal of `knowledge_set_status` names the page and the tenant of the
  call in its octo-cli command (`octo-cli -c SetKnowledgePageStatus -s <slug> -st Verified -tid <tenant>`, tenant
  resolved like the tool resolves it, without opening it; the placeholder stays when it cannot be resolved or is not a
  plain tenant id, so no caller text reaches the copy-paste command), so a person
  following it no longer verifies the page in whatever tenant octo-cli is set to. `knowledge_search` scoring counts
  literal hits only on whole words: a query word inside another word ("use" in "because", "run" in "truncate") is no
  hit, so a page the text index preselected without the word is dropped. Tests in `Tools/KnowledgeToolsTests`.
- **AB#8717** — the build failed with CS0419 (ambiguous cref) after the engine added a second
  `RtSecurityContext.ForUser` overload (AB#8574); the XML doc comment in `RuntimeSecurityContextResolver` now names the
  two-parameter overload it means.

### Security

- **AB#8702** — the content guard of knowledge writes also refuses GitHub tokens (`ghp_`, `gho_`, `ghu_`, `ghs_`, `ghr_`,
  `github_pat_`), AWS access key ids (`AKIA…`, `ASIA…`) and credential assignments (`password=…`, `DB_PASSWORD="…"`,
  `"client_secret": "…"`, `api_key: …`, `access_token = …`) with `ContentRejected`, nothing written (new detectors
  `GitHubToken`, `AwsAccessKey`, `CredentialAssignment`). Prose that names a credential without a value, placeholders
  (`$VAR`, `<your-password>`, `${{ secrets.X }}`, `***`), type names and code pass. The local AB#8486 run had stored a page
  with `password=…` and a `ghp_` token. Tests in `Services/ContentGuardTests` and `Tools/KnowledgeToolsTests`.

### Added

- **AB#8719** — `knowledge_upsert_page` takes the optional run data `latencySeconds` (end-to-end generation latency of the
  ingest run) and stores it on the new `KnowledgePageVersion` as `LatencySeconds`. Omitted = not stored; a negative or
  non-finite value is refused with `InvalidArgument`. The live ingest on kb-test (AB#8486) left `LatencySeconds` empty on every
  version because the tool had no such parameter. Tests in `Tools/KnowledgeToolsTests`.

- **AB#8488** — new tool `knowledge_write_lint_report` (medium risk): writes one System.Ai `KnowledgeLintReport` per
  call for the nightly knowledge lint (findings Contradiction, Orphan, Stale, MissingLink, UnsupportedClaim,
  CitationNotInSource; run data tokens, cost, `verifiedThrough`, `runRef`). Content guard over every text, always
  `IsValidated` false, no page changed. The generic write tools refuse the report type
  (`GENERIC_WRITE_REPLACED_BY_KNOWLEDGE_TOOL`, `tool` `knowledge_write_lint_report`). Tests in
  `Tools/KnowledgeLintToolsTests`.

- **AB#8712** — every successful `knowledge_search` response carries `answeringConventions`: the "Answering questions"
  section of the tenant's schema page `knowledge-schema` (read with the caller's session, only from a Verified page, at most 2000
  characters, longer text cut deterministically at a word boundary with a marker), or a built-in default when the tenant
  has no such page or section or the page is not Verified, and `answeringConventionsSource` (`knowledge-schema@<version>` or `default`). The bounded text is cached per
  tenant and page version (`KnowledgeConventionsCache`); a new version of the schema page is read again. The tool
  description says so. Clients cite pages and do not invent without reading the schema page first.
  Tests in `Tools/KnowledgeToolsTests`.

- **AB#8657** — `knowledge_upsert_page` takes the run data of the ingest pipeline as optional parameters and stores them on
  the new `KnowledgePageVersion`: `inputTokens` (`PromptTokens`), `outputTokens` (`CompletionTokens`), `cost` in USD
  (`CostUsd`), `reviewerRounds` (`ReviewerIterations`) and `runRef`, the pipeline execution id (`RunRef`). Omitted values are
  not stored, existing callers are unchanged; negative or non-finite numbers and a blank `runRef` are refused with
  `InvalidArgument`; `runRef` passes the content guard. Cache read/creation tokens wait for System.Ai attributes.
  Tests in `Tools/KnowledgeToolsTests`.

- **AB#8529** — the model-changing tools `import_ck_model`, `install_blueprint`, `update_blueprint`, `uninstall_blueprint`,
  `refresh_ck_catalogs`, `import_ck_from_catalog` and `fix_all_ck_models` name the required tenant role `DataModelManagement` in
  their descriptions and return the service's problem code in the new `errorCode` member (all asset and file-transfer tool
  responses), e.g. `DataModelManagementRequired` when the asset repository refuses the caller (r3.5.67). The error message
  is the service's reason plus status and code. Test `Tools/DataModelManagementRequiredTests`.

- AB#8484: knowledge base tools `knowledge_search`, `knowledge_get`, `knowledge_upsert_page`, `knowledge_link`,
  `knowledge_restore_version`, `knowledge_set_status` and the resource template `knowledge-page://{tenantId}/{slug}` over
  the System.Ai 4.7.0 knowledge types (needs octo-ai-services AB#8483). Every page write appends a `KnowledgePageVersion`
  (author, time, change note, run metadata) and runs the content guard; `knowledge_upsert_page` takes the generation JSON
  of the KnowledgeCapture prototype unchanged and links cited SourceArtifacts with `GeneratedFrom`; content writes set the
  page back to Draft; only a user principal may set Verified. The guard also checks the change note and run metadata,
  and `knowledge_set_status` runs it as well.
- AB#8475: content guard for knowledge writes (new library `src/McpServices.ContentGuard`, registered as `IContentGuard`).
  It refuses text with an Anthropic API key (`sk-ant-<type>-`), a PEM block, a JWT, a high-entropy token or a value
  equal to a Secret attribute value the caller could reveal in the tenant, with the error code `ContentRejected`; the
  refusal names detectors and fields, never the content, and is logged with tenant, caller and operation. Tenant
  secrets reach the guard only as SHA-256 fingerprints (`Services/ContentGuard/TenantSecretFingerprints`, read with the
  caller's session from every entity, paged without a cap, needs the key ring). The knowledge tools of AB#8484 are its first caller.
- AB#8346: sibling-tenant grants. `lookup_source_tenant_user` (Medium) finds one user of a source tenant by exact user
  name or e-mail (identity AB#8341); the identifier is passed to the service only and never echoed; every miss answers
  `errorCode: SourceUserNotFound`, a rate-limited call `SourceUserLookupRateLimited` with `retryAfterSeconds`.
  `create_external_tenant_user_mapping` takes `groupIds` (target-tenant groups, AB#8343) and returns the mapping as
  stored (user name from the source tenant, group names) with its `rtId`; refusals carry `errorCode`
  (`SourceTenantNotPermitted`, `UnknownGroup`, `ExternalTenantUserMappingAlreadyExists`) and one readable message.
  `get_cross_tenant_grant_permissions` (Low) and `set_cross_tenant_grant_permissions` (High, `confirm=true`, reports
  added/removed tenants) manage the source tenant's allow-list (AB#8345). Needs octo-sdk with AB#8346.

### Changed

- AB#8346: `create_external_tenant_user_mapping`, `update_external_tenant_user_mapping` and
  `delete_external_tenant_user_mapping` are **High** risk (were Medium): they grant, change the roles of and remove
  access of users of other tenants. Workers that cached the risk registry pick it up at
  the next session start.
- AB#8440: `add_user_to_role` and `remove_user_from_role` call the CK methods `System.Identity/User.AddRole-1` and
  `User.RemoveRole-1` through the generated `RtUserMethodClient` instead of the REST methods `AddRoleToUser` /
  `RemoveRoleFromUser`, which octo-sdk marks `[Obsolete]` (AB#8416). Tool names, parameters and success output are
  unchanged. The client is generated in the new project `src/McpServices.MethodClients` from
  `System.Identity-[3.2,4.0)` (`construction-kits.yaml`); the tools resolve the user name or e-mail to the user's rtId
  first (so an e-mail address now works for both tools). Error texts: `Role with name '…' not found.`,
  `User '…' already has role '…'.`, `User '…' doesn't have role '…' as a direct role.` (a role held only through a group
  is reported, before the REST call answered success without a change), platform errors as `{Code}: {message}`
  (e.g. `METHOD_FORBIDDEN: …`). `create_user` and `reset_user_password` stay on REST until AB#8418. Needs octo-sdk with
  AB#8416 (ee83237) and an asset repository and identity service with the User role methods (System.Identity 3.2.0).

### Fixed

- **AB#8567** — `knowledge_upsert_page` keeps the summary when it is omitted on an update (it was cleared, while `kind` was
  kept). Same rule for every optional content parameter: an omitted `kind`, `summary`, `citations` or `hallucinationFlags`
  keeps the current value, an explicit `""` / `[]` clears it; before, omitted citations and flags were cleared as well.
  The tool description names the exceptions (`origin` only for new pages, `linksTo` / `sourceRtIds` only add,
  `changeNote` and run metadata per version).
- **AB#8566** — `knowledge_search` with a query no longer returns pages that contain none of its words (score 0). A query
  that matches nothing returns 0 pages, `isSuccess: true` and the "No knowledge page matches" message, also with a status
  or kind filter (the hand test returned `knowledge-schema` for "Zebrafisch" + Verified). Words shorter than 3 characters
  are ignored; a query without a longer word returns the index of all pages, and the message says it is not a match.
  The score also counts words with the same English stem as a query word (plural, -ing, -ed), like the stemming text
  index does, so "pipelines" still finds a page that says "pipeline", and "using" a page that says "use". Query words are
  split like the page text (hyphens, brackets, apostrophes), so "zebrafisch-pipelines" counts both words.
- AB#8355: `execute_runtime_query` and `execute_stream_data_query` answer "not found" for a deleted persisted query.
  The default delete archives the query (`rtState = Archived`) and the lookup by id still returned it, so a deleted
  query kept running. Both tools now load the query with the engine's `GetActiveRtEntityByRtIdAsync`.

### Security

- **AB#8565** — `knowledge_set_status` refuses `Verified` for every MCP caller (user token, client-credentials token, no
  principal) with `errorCode` `VerifiedNotAllowedOverMcp`, before the tenant is opened; nothing is written (decision G2a:
  MCP callers are always AI clients, also in a user's session). Draft, InReview and Rejected work as before. The tool and
  `status` parameter descriptions list only those three and name the human path: octo-cli `SetKnowledgePageStatus`
  (octo-cli AB#8565) or GraphQL. Replaces the user-principal check and its code `VerifiedRequiresPerson`. New test
  `Services/Knowledge/KnowledgeContentHashGoldenTests` pins the content hash octo-cli recomputes; `KnowledgePages.Render`
  ends lines with an explicit `\n` instead of `AppendLine`, so the hash no longer depends on the OS (unchanged on Linux).
- **AB#8568** — the generic entity tools no longer write System.Ai knowledge pages past versioning and the content
  guard. `create_entity`, `create_entity_with_secrets`, `update_entity`, `set_entity_secrets` and `delete_entity` refuse
  `System.Ai/KnowledgePage`, `System.Ai/KnowledgePageVersion` and types derived from them with `errorCode`
  `GENERIC_WRITE_REPLACED_BY_KNOWLEDGE_TOOL` (`errorParameters` `ckTypeId`, `tool`), checked on the requested type before
  a session is opened and again on the actual type of the stored entity; `import_runtime_model` refuses a file (YAML,
  JSON or zip) that writes a knowledge type (also a derived one, judged through the CK type hierarchy of the target tenant)
  or the knowledge associations `LinksTo`, `GeneratedFrom`, `VersionOf` before it reaches the asset repository. The check
  reads the decoded ids (the importer's streaming JSON reader, YAML parser events, zip entries streamed and capped), so
  escapes, quoting, aliases and casing do not hide a reference; a file it cannot read, or with an extension other than `.zip`, `.json`, `.yaml` or `.yml` (`.yml` is read as YAML), is refused with a generic message; in a zip every `.json`, `.yaml` and `.yml` entry is checked. Nothing is written. Delete is refused too (CPO decision 2026-11-08): agents set
  an unwanted page to `Rejected` with `knowledge_set_status`; a person deletes pages outside MCP (GraphQL or octo-cli).
  Reads are unchanged. The check and the import run on one private snapshot of the upload, in a fresh owner-only
  directory with an unpredictable name per import (created atomically and so owned by the service user, verified on Unix
  with managed APIs only to be a real directory closed to group and others - no native `lstat` check, deleted with its content on every exit path), so a delayed PUT to the upload slot
  cannot swap the file after the check (CWE-367) and a directory another user prepared in `/tmp` is never used. The YAML
  check refuses files nested deeper than 64 levels or retaining more than 16 MiB of anchored values (DoS). Tests `Tools/KnowledgeGenericWriteRefusalTests`.
- AB#8043: the generic CRUD, query and aggregation tools enforce the CK attribute access rules, like the asset-repo
  GraphQL guards. Before, they returned `Hidden` values (password hash, security stamp, authenticator tokens of
  `System.Identity/User`), accepted filters and group-by on them and wrote `Hidden` / `MethodOnly` / `ReadOnly` and
  identity-owned User fields. Now: every entity is mapped by `AccessAwareRtEntityToDtoMapper` (Hidden attributes
  removed, records included); filters, aggregations, group-by and stored-query paths on Hidden attributes are refused
  with `ATTRIBUTE_NOT_QUERYABLE` (also by `execute_runtime_query` and when storing a query); writes of Hidden /
  MethodOnly attributes, of ReadOnly ones on update and of the identity-owned User fields (`IdentityOwnedWrites`) are
  refused with `ATTRIBUTE_NOT_WRITABLE`. Rules come from the engine helpers (`AttributeAccess`, `IdentityOwnedWrites`,
  `ArchiveHiddenColumnGuard.FindHiddenAttribute`).

### Changed

- AB#7362: `query_stream_data_downsampling` cuts local-day bins: optional `timeZone` (IANA id), `binIntervalDays`
  (1 to 10,000 whole days) and `binOrigin` (`HH:mm`, local time a bin starts). With any of them the bins are local
  calendar days of the zone (UTC when only interval or origin is given), 23, 24 or 25 hours across DST changes, and
  `limit` is ignored; a day without values comes back with count 0 (engine AB#7360). Gas day: `Europe/Vienna`,
  `06:00`, 1 day. An invalid origin, interval or time zone returns `IsSuccess=false` with the validation message
  before any query; calls without the new parameters send the same engine options as before.
  `get_rollup_query_metadata` returns `bucketAlignment`, `referenceTimeZone` (as stored, like GraphQL
  `rollupsForArchive`; only effective with a calendar alignment), `dayStartOffset` (`HH:mm`) and `dayStartOffsetMs`
  (both null for midnight days).

- AB#7991: `install_blueprint` and `update_blueprint` return the archive activations of the blueprint seed
  (`Result.ArchiveActivations`: per archive `Outcome` `Activated` / `SkippedPreExisting` / `SkippedOptOut` /
  `SkippedStreamDataDisabled` / `Failed`, `Reason`, and for new rollups `Backfill` and `BackfillJobRtId`) and the
  warnings, and the `Message` summarises them. Before, the SDK result dropped the field; `update_blueprint` also returned
  no result at all (new `Result` property, the update counts and warnings included). Needs the SDK with
  `BlueprintArchiveActivationDto` (octo-sdk, AB#7991).

- AB#7119: `create_entity` and `create_entity_with_secrets` refuse a CK type whose static method replaces the
  generic create (`replacesGenericCreate`, own or inherited) with `ErrorCode = GENERIC_CREATE_REPLACED_BY_METHOD`
  and `ErrorParameters` `ckTypeId` / `methodId`, the same contract as the GraphQL create. The check runs before
  any write, so no entity and no secret is written; the error contains no entity data. Tool descriptions mention
  that such types are created through their method. Other types are unaffected.

- AB#7448: the generic-create lookup uses the shared engine helper `GenericCreateReplacement` (ConstructionKit.Contracts)
  instead of an own copy (fixes the ambiguous-reference build break on main); behaviour unchanged.

### Fixed

- AB#8277: `KnowledgeUrlFetcherTests.Fetch_AbsoluteLocalPath_RefusedAsFileScheme` passes on Windows. `/etc/passwd` is
  refused by the scheme check on Linux/macOS (parses as `file://`) and by the absolute-URL check on Windows; the test
  now accepts either refusal message and still asserts that no body is returned and no HTTP request is sent. Test-only,
  `KnowledgeUrlPolicy` is unchanged.

- AB#5346: the stream-data aggregation tools (`query_stream_data_aggregation`, `_grouping`, `_downsampling` and
  `execute_stream_data_query`) read only the engine's real result keys: group columns by storage name (`obiscode`,
  `amountunit`), aggregations as `{column}_{function}` (`amountvalue_sum`, `amountvalue_count`). The values themselves
  were fixed with AB#7919; the `{Function}({path})` fallback, a key no engine version writes, is removed. A stream-data
  `count` without `attributePath` is refused up front with a message naming the alternative (count the measured
  attribute, or `rtId` for raw rows); before, the engine refused it as the unknown column `'*'`. Runtime aggregations
  keep `count` without a path. Tests mock the real engine keys, including COUNT on the dotted mixed-case path
  `Amount.Value`.

- AB#8043: the generic-create refusal (AB#7119) looked the type up with its full id (`Model-x.y.z/Type-n`), which
  the CK cache does not key by, so it never fired; it now looks up the runtime id. With it, `create_entity` refuses a
  new `System.Identity/User` (`GENERIC_CREATE_REPLACED_BY_METHOD`, method `User.CreateUser-1`).

- AB#8017: `provision_current_user_as_admin` names the identity the mapping was written for (`SourceTenantId`,
  `SourceUserId`, `SourceUserName` and the message). From a session scoped to an intermediate tenant the identity
  service now provisions the home identity of the user (the identity later tool calls exchange from) instead of
  the cross-tenant shadow user, so a nested (grandchild) tenant is reachable after the call. Needs octo-sdk with
  the mapping returned by `ProvisionCurrentUser` and identity with AB#8017.

- AB#7282: tenant-management tools (`get_tenants`, `create_tenant`, `delete_tenant`, `clean_tenant`,
  `attach_tenant`, `detach_tenant`, `clear_tenant_cache`, `update_system_ck_model`) and
  `export_runtime_graphql_sdl` now exchange the caller's token for the addressed (route/parent) tenant via
  the RFC 8693 path instead of sending the home token. This makes them work on a non-home parent tenant under
  user-token Enforce (the caller needs access to that parent tenant). A failed exchange returns
  `IsSuccess = false` with a "token exchange for tenant" error and makes no backend call; the home token is
  never used as a fallback.
