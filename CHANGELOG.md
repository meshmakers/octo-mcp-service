# Changelog

## Unreleased

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

- AB#7119: `create_entity` and `create_entity_with_secrets` refuse a CK type whose static method replaces the
  generic create (`replacesGenericCreate`, own or inherited) with `ErrorCode = GENERIC_CREATE_REPLACED_BY_METHOD`
  and `ErrorParameters` `ckTypeId` / `methodId`, the same contract as the GraphQL create. The check runs before
  any write, so no entity and no secret is written; the error contains no entity data. Tool descriptions mention
  that such types are created through their method. Other types are unaffected.

- AB#7448: the generic-create lookup uses the shared engine helper `GenericCreateReplacement` (ConstructionKit.Contracts)
  instead of an own copy (fixes the ambiguous-reference build break on main); behaviour unchanged.

### Fixed

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
