# Changelog

## Unreleased

### Fixed

- AB#7282: tenant-management tools (`get_tenants`, `create_tenant`, `delete_tenant`, `clean_tenant`,
  `attach_tenant`, `detach_tenant`, `clear_tenant_cache`, `update_system_ck_model`) and
  `export_runtime_graphql_sdl` now exchange the caller's token for the addressed (route/parent) tenant via
  the RFC 8693 path instead of sending the home token. This makes them work on a non-home parent tenant under
  user-token Enforce (the caller needs access to that parent tenant). A failed exchange returns
  `IsSuccess = false` with a "token exchange for tenant" error and makes no backend call; the home token is
  never used as a fallback.
