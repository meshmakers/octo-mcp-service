# Changelog

## Unreleased

### Changed

- AB#7119: `create_entity` and `create_entity_with_secrets` refuse a CK type whose static method replaces the
  generic create (`replacesGenericCreate`, own or inherited) with `ErrorCode = GENERIC_CREATE_REPLACED_BY_METHOD`
  and `ErrorParameters` `ckTypeId` / `methodId`, the same contract as the GraphQL create. The check runs before
  any write, so no entity and no secret is written; the error contains no entity data. Tool descriptions mention
  that such types are created through their method. Other types are unaffected.

### Fixed

- AB#7282: tenant-management tools (`get_tenants`, `create_tenant`, `delete_tenant`, `clean_tenant`,
  `attach_tenant`, `detach_tenant`, `clear_tenant_cache`, `update_system_ck_model`) and
  `export_runtime_graphql_sdl` now exchange the caller's token for the addressed (route/parent) tenant via
  the RFC 8693 path instead of sending the home token. This makes them work on a non-home parent tenant under
  user-token Enforce (the caller needs access to that parent tenant). A failed exchange returns
  `IsSuccess = false` with a "token exchange for tenant" error and makes no backend call; the home token is
  never used as a fallback.
