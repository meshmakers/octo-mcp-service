### Generic create replaced by a CK method (AB#7119)

`create_entity` and `create_entity_with_secrets` (both go through `CreateCoreAsync`) refuse a CK type that has a
static method with `replacesGenericCreate: true` (declared on the type or inherited), exactly like the GraphQL
create of `octo-asset-repo-services` (`GenericCreateReplacedByMethodGraphQlError`). The lookup is the shared engine helper
`GenericCreateReplacement` (`Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph`, AB#7121; the same helper
serves the asset repository and blueprint apply, AB#7448 — never keep a private copy). It reads the type graph from the
tenant's CK cache by the version-less `RtCkId` and uses `AllMethods` (inherited methods included); `GenericCreateRefusal`
(`Services/`) only shapes the MCP response around it. The refusal runs **before** any secret handling or write, so no entity and
no secret is written, and the response carries ids only (no entity data, no secret value):
`IsSuccess = false`, `ErrorCode = GENERIC_CREATE_REPLACED_BY_METHOD`, `ErrorParameters` = `ckTypeId` (the type the
caller tried to create) and `methodId` (version-less qualified method id of the replacing method). A type that is
not in the CK cache is not refused (the create fails on its own). Tool names, inputs and the happy path are
unchanged; no type sets the flag today. Tests: `RuntimeEntityCrudToolsGenericCreateTests` (builds type graphs with
methods via `CkGraphTestBuilder.BuildTypeWithMethods`).

## CK attribute access in the generic tools (AB#8043)

Family-2/3 tools read and write the tenant repository directly, and **the repository does not know CK `access`**
(CK v2, AB#5668). The asset-repo GraphQL enforces it in its own guards (`AccessQueryGuard`, `IdentityOwnedWriteGuard`,
`StoredQueryWriteGuard`), so this server must enforce the same rules itself. Before AB#8043 it did not: `get_entity_by_id`
on a `System.Identity/User` returned `passwordHash`, `securityStamp` and `userTokens`, a filter on `passwordHash` was an
equality oracle, and `update_entity` wrote `securityStamp` / `twoFactorEnabled`.

- **Output — `Services/AccessAwareRtEntityToDtoMapper`.** The only `IRtEntityToDtoMapper` (Program.cs). It maps with the
  SDK `RtEntityToDtoMapper` and removes every attribute whose assignment is `Hidden`
  (`AttributeAccess.IsReadableOutsideMethod`), on the entity and inside records / record arrays. The SDK mapper hands
  record arrays out as a lazy projection, so the wrapper materialises the list before stripping. Every tool that maps an
  entity is covered at once (CRUD, query, associations, `execute_runtime_query`, conflict payloads, knowledge resources).
  🔴 Never register or construct the bare SDK mapper — `Architecture/HiddenAttributeOutputArchitectureTests` pins it.
- **Queries — `Services/AttributeAccessGuard.FindHiddenUsage`.** Filters (every operator, `IsNull`/`IsNotNull` too: they
  tell whether a value is set), aggregations and group-by of `query_entities`, `query_entities_simple`,
  `navigate_associations`, `get_association_tree`, `query_entities_aggregation`, `query_entities_grouping` and the extra
  filters of `execute_runtime_query` are refused with `ATTRIBUTE_NOT_QUERYABLE: Attribute '<path>' of '<type>' is hidden
  and cannot be used for <operation>.` before a session is opened. A path reaches Hidden through any segment or by ending
  at a record that holds a Hidden sub-attribute (engine `ArchiveHiddenColumnGuard.FindHiddenAttribute`); it is checked
  against the queried type and every derived type (a full id is converted to the runtime id the cache keys by,
  `AttributeAccessGuard.ToRuntimeId`); an unknown type is checked by name against all Hidden assignments of the tenant
  and against record-valued attributes whose record reaches Hidden (fail closed). The Hidden check runs before the Secret
  check, so an attribute that is both gets `ATTRIBUTE_NOT_QUERYABLE`. Projection paths (`attributePaths`) are not refused — the output mapper removes Hidden values.
- **Stored queries — `FindHiddenStoredQueryPath`.** A `System/PersistentQuery` (or derived) entity whose `FieldFilter`,
  `Sorting`, `Columns`, `GroupingColumns` or `AttributeSearchFilter` reach a Hidden attribute of its `QueryCkTypeId` is not
  stored by `create_entity` / `update_entity` (checked on the effective entity after the assignment, transaction aborted)
  and not run by `execute_runtime_query` (AB#8049 parity: the repository applies a stored filter as it is).
- **Deleted stored queries (AB#8355).** `execute_runtime_query` and `execute_stream_data_query` load the query with the
  engine's `GetActiveRtEntityByRtIdAsync`: a deleted (archived) query answers "not found" like a missing one.
- **Writes — `FindNotWritable`.** `create_entity`, `create_entity_with_secrets`, `update_entity` and `set_entity_secrets`
  (entity data, secrets and `clearSecretAttributes`) refuse `Hidden` and `MethodOnly`, and `ReadOnly` on update
  (`AttributeAccess.IsGenericallyWritable(access, isCreate)`; every path segment counts), with `ATTRIBUTE_NOT_WRITABLE`.
  The identity-owned `System.Identity/User` fields are `MethodOnly` in System.Identity 3.0.0 and refused by this rule (AB#8053; the
  engine list `IdentityOwnedWrites` holds only the identity associations now, which these tools cannot write; a tenant on 2.x has no
  protection until the identity service has imported 3.0.0). `update_entity` judges the write again against the entity's actual type after loading it
  (the caller may name a base type of a derived entity, e.g. a User). No association write exists in these tools, so the
  identity association list does not apply.
- **New users.** `create_entity` of a new `System.Identity/User` is refused by the generic-create refusal below (engine
  runtime rule: `User.CreateUser-1` replaces the generic create). That refusal used to look the type up with its full id,
  which the CK cache (keyed by the version-less runtime id) never matched — it now looks up the runtime id.
- **No own lists.** All rules come from the engine (`AttributeAccess`, `IdentityOwnedWrites`, `GenericCreateReplacement`,
  `ArchiveHiddenColumnGuard`). Messages carry paths and type ids, never a value.
- **Not affected:** family-1 tools (they go through the backend APIs, which guard themselves — e.g. `export_runtime_model_*`
  runs the asset/bot export, which never emits Hidden values), stream-data tools (archives cannot hold a Hidden column,
  engine guard on activation). `get_type_schema` still lists Hidden attributes by name (no values).
- Tests: `Tools/RuntimeEntityAccessGuardTests` (a User-shaped model with Hidden / MethodOnly / ReadOnly attributes, a
  record with a Hidden sub-attribute, a derived-type case; the cache mock resolves ids like the real cache, by runtime id
  only) and `Architecture/HiddenAttributeOutputArchitectureTests`.

## Paging of the entity query tools (AB#8904)

The family-2/3 tools read the engine directly, so the asset-repo API guard against unbounded queries (AB#8898,
`RuntimeQueryGuard`, MaxPageSize 1000) never sees them. They bound themselves through `Services/QueryPaging.cs`:

- **Tools**: `query_entities`, `query_entities_simple` (`limit`/`offset`), `navigate_associations` (`take`/`skip`),
  `get_association_tree` (`rootTake`/`rootSkip`, root entities only) and `execute_runtime_query` (`take`/`skip`,
  applied to `RtSimpleRtQuery`).
- **Rules**: no page size = `DynamicToolOptions.GetEffectiveDefaultQueryLimit()` (100); a page size above
  `GetEffectiveMaxQueryResultLimit()` (1000) is **refused, never capped** (decision of Feature AB#8793), with the message
  `<arg> <n> exceeds the maximum of <max>; page with <skipArg> (...)`; a page size below 1, a negative skip and a
  skip above `int.MaxValue - max` (the next page's skip must fit an int) are refused.
  `QueryPaging.Resolve` runs **before** the session is opened or anything is loaded (`execute_runtime_query`: before the
  persisted query is read), so a refused call never reaches the engine.
- **Config**: `MaxQueryResultLimit` can be lowered, never raised: `DynamicToolOptions.MaxPageSize` (1000) is a constant,
  a higher configured value is ignored, logged at startup and reported by `DynamicToolHealthCheck` (Degraded). There is
  no override flag on purpose.
- **Response**: every result carries `Paging` (`QueryPagingInfo`: `skip`, `take`, `defaultTakeApplied`, `maxTake`,
  `returnedCount`, `hasMore`, `nextSkip`, `hint`). `hasMore` comes from the engine's `TotalCount`.
- **Not paged**: `query_entities_aggregation` / `query_entities_grouping` (no entities; their tests pin `null` skip/take)
  and the children per level of `get_association_tree` (open follow-up under AB#8793).
- A new tool that returns runtime entities must use `QueryPaging` and append `RuntimeEntityCrudTools.PagingDescription`
  (or an equivalent sentence) to its description. Tests: `Tools/RuntimeQueryPagingTests` (one theory per rule over all
  five tools).
