# Changelog

## Unreleased

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
