# Changelog

## Unreleased

### Added

- AB#8475: content guard for knowledge writes (new library `src/McpServices.ContentGuard`, registered as `IContentGuard`).
  It refuses text with an Anthropic API key (`sk-ant-<type>-`), a PEM block, a JWT, a high-entropy token or a value
  equal to a Secret attribute value the caller could reveal in the tenant, with the error code `ContentRejected`; the
  refusal names detectors and fields, never the content, and is logged with tenant, caller and operation. Tenant
  secrets reach the guard only as SHA-256 fingerprints (`Services/ContentGuard/TenantSecretFingerprints`, read with the
  caller's session, needs the key ring). The knowledge tools of AB#8484 are its first caller; no tool uses it yet.

### Changed

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

- AB#8355: `execute_runtime_query` and `execute_stream_data_query` answer "not found" for a deleted persisted query.
  The default delete archives the query (`rtState = Archived`) and the lookup by id still returned it, so a deleted
  query kept running. Both tools now load the query with the engine's `GetActiveRtEntityByRtIdAsync`.

### Security

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
