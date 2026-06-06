# Optimistic Locking on Entity Writes — Design Spike (#4128)

**Status:** Spike — pick the option below before implementation lands.
**Owner:** AI Adapter track. ADR-17 in `octo-ai-services/docs/concepts/octo-ai-adapter.md`.
**Related:** #4111 Phase A (risk-classification, shipped `88a2072` on `octo-mcp-service/main`).

## 1. Why this spike exists

The Phase-1 AI Adapter ships parallel sessions per tenant (concept §5b). When two
sessions edit the same CK entity, the second writer must lose deterministically
and reason about the conflict instead of silently clobbering the first writer's
work. `update_entity` and `delete_entity` in `RuntimeEntityCrudTools.cs` need
an `expected_version` parameter and a structured `ConflictError` return shape.

The CK Engine does not expose an entity-level version field _to the MCP layer_
today, even though the underlying infrastructure already carries one. This spike
documents the three implementation options, the codebase facts that constrain
each, and the recommended path.

## 2. Codebase facts (don't repeat the research)

The Explore subagent collected these. They are load-bearing for the analysis
below — every recommendation rests on them.

1. **`RtEntity.RtVersion: ulong` already exists** as a base infrastructure
   property on every CK entity. Declared in
   `octo-construction-kit-engine/src/Runtime.Contracts/RepositoryEntities/RtEntity.cs:79`.
   Sibling of `RtCreationDateTime`, `RtChangedDateTime`, `RtState`.
2. **The MongoDB layer auto-increments `rtVersion` on every write.** Proven by
   `octo-construction-kit-engine-mongodb/src/Runtime.Engine.MongoDb/Repositories/MongoDb/MongoDataSourceMapper.cs:56`
   doing `Builders<TEntity>.Update.Inc("rtVersion", 1)`. No application-level
   work needed to maintain the counter — it's already there for every entity in
   every tenant database in production.
3. **`rtVersion` is queryable and projectable.** `SingleOriginRtQuery.cs`
   projects it (lines 275/431/556); `RtEntityFieldFilterResolver.cs` registers
   it as a filterable system field (lines 36/54/84). Read tools that return
   `RtEntity` instances already carry the field on the wire.
4. **Conditional-update infrastructure exists and is tested.**
   `AttributeNewerThanGuard` (`octo-construction-kit-engine/src/Runtime.Contracts/AttributeNewerThanGuard.cs`)
   + `EntityUpdateInfo<TEntity>.CreateConditionalUpdate(rtEntityId, rtEntity, guard)`
   gate writes on a server-side filter; integration tests in
   `octo-construction-kit-engine-mongodb/tests/Runtime.Engine.MongoDb.IntegrationTests/ConditionalUpdateTests.cs`
   verify that stale writes are silently dropped when the guard fails. The
   guard is composed into the existing `FilterDefinition<T>` the Mongo layer
   builds for the `ReplaceOneAsync` call — no schema change.
5. **`AiTokenLease.Generation` is a precedent in `octo-ai-services`.** A
   service-side counter bumped on every refresh, used to gate concurrent-refresh
   atomicity. Confirms the pattern is acceptable in OctoMesh, but lives at the
   CK-attribute layer for that specific type — not on the base Entity.
6. **CK model migrations are routine.** Auto-detected on `ImportCkModelAsync`
   via `ICkModelUpgradeService`; migration scripts in
   `octo-construction-kit-engine/src/SystemCkModel/ConstructionKit/migrations/`
   declarative YAML. Per-tenant cost is one Update step at import time.
7. **MCP failure shape today.** `RuntimeEntityCrudTools.UpdateEntity` (around
   line 297) currently throws `ArgumentException` on missing entity, mapped to a
   plain `ErrorMessage` string in `UpdateEntityResponse`. There is no
   structured-error variant yet; adding `ConflictError` is a response-envelope
   extension.

## 3. The three options

### Option 1: Content-hash version

**Mechanism.** Server computes a stable hash of the entity content on read,
returns it as `version` (string). On write, worker echoes it back. Server reads
the current entity, recomputes the hash, compares. If mismatch → reject.

**Pros.**
- No engine change. Pure MCP-layer.
- Tenant-agnostic — no migration.

**Cons.**
- _Schema-agnostic hash is fragile._ Need a canonical normalisation: attribute
  order, null vs missing, DateTime kind (`Unspecified` vs `Utc`), numeric
  representation (long vs int as in `JsonScalar.ToClr`), `RtCkId` virtual-
  property shape (`RtCkIdJsonShim`). Any normalisation bug becomes a false-
  conflict bug, which is much worse than the original race.
- Two MongoDB round-trips per update (read-then-compare-then-write) without a
  durable transaction across them. Two parallel writers can both pass the
  pre-write check and still race at the actual write step — fundamentally
  doesn't fix the lost-update problem, only narrows the window.
- The hash has no meaning outside the MCP server; cannot be used downstream
  (audit log, UI diff, billing) for any other purpose.

**Verdict.** Reject. Solves the wrong sub-problem at the wrong layer.

### Option 2: Counter attribute on a base CK type

**Mechanism.** Add a `Version` (or `Revision`) CK attribute to `System/Entity`
in the CK model. MCP server bumps on update, checks on update.

**Pros.**
- Counter semantics, no normalisation issues.
- Visible in CK queries, dashboards, audit log.

**Cons.**
- _Redundant._ `RtVersion: ulong` already exists at the infrastructure layer
  (fact 1) and is auto-bumped by Mongo (fact 2). Adding a parallel
  CK-declared `Version` attribute means two counters that can drift out of
  sync if anything ever writes the entity without going through the
  `UpdateOneRtEntityByIdAsync` path.
- Cross-tenant CK-model migration cost when none is actually necessary.
- The CK attribute would be at the wrong layer — it's persistence metadata,
  not domain content.

**Verdict.** Reject _as stated_. The intent is already covered by the
existing infrastructure field; declaring it twice creates a maintenance
liability.

### Option 3: Engine-level row version (recommended path)

**Mechanism.** Use the existing `RtVersion` infrastructure (facts 1–3) +
extend the existing conditional-update guard pattern (fact 4) with an
equals-check variant.

Two repositories see one change each:

1. **`octo-construction-kit-engine`** — new guard record
   `AttributeEqualsGuard(string AttributePath, object ExpectedValue)` (or
   `RtVersionGuard(ulong ExpectedVersion)` for the typed case) and the
   matching `EntityUpdateInfo<TEntity>.CreateConditionalUpdate(...)` overload
   that the Mongo layer translates into
   `Builders<T>.Filter.Eq("rtVersion", expected)`. The existing
   `ConditionalUpdateTests` shape is the reference.
2. **`octo-mcp-service`** — `update_entity` / `delete_entity` gain an
   optional `expected_version` ulong parameter. When passed, the tool calls
   `CreateConditionalUpdate` with the new guard. When the update affects zero
   rows (the existing guard-failure path), the tool re-reads the current
   entity, projects its `rtVersion` + payload, and returns
   `ConflictError { current_version, current_payload }` as a structured
   variant in the existing `UpdateEntityResponse`. `get_entity_by_id` /
   `query_entities` include `rt_version` in the response. Backward-compat:
   no `expected_version` → no guard → current behaviour.

**Pros.**
- _Zero CK model migration._ Every existing tenant's entities already carry
  the counter (fact 2).
- _Zero domain model change._ `RtVersion` is persistence metadata; the CK
  model stays clean.
- _Cheap, tested mechanism._ The guard pattern + Mongo-filter-on-update is
  proven; this is one more variant. The hot path stays a single
  `ReplaceOneAsync` with a richer filter, no extra round-trip.
- _Conflict response carries information._ The worker can render
  `current_payload` in a diff view (concept §9 approval-style escalation)
  without a second tool round-trip.
- _The counter is queryable and projectable_ — read tools already return it
  on the wire (fact 3); no new projection work.

**Cons.**
- Cross-repo change. Two PRs (`octo-construction-kit-engine`,
  `octo-mcp-service`) instead of one. Mitigated by the engine PR being a
  ~50-line additive guard.
- The guard pattern is generic; we'd be adding the second variant
  (`AttributeNewerThanGuard` was first). Worth standardising the name — see
  open question 1 below.

**Verdict.** Recommended.

## 4. Recommendation

Implement **Option 3** as a two-PR roll-out:

### PR 1 — `octo-construction-kit-engine`

- New record `AttributeEqualsGuard(string AttributePath, object ExpectedValue)`
  in `Runtime.Contracts/`. Mirrors `AttributeNewerThanGuard`.
- New `EntityUpdateInfo<TEntity>.CreateConditionalUpdate(...)` overload
  accepting `AttributeEqualsGuard`.
- MongoDB layer: extend the filter-builder in
  `Runtime.Engine.MongoDb/Repositories/MongoDb/Mutation.cs` (or wherever the
  existing `AttributeNewerThanGuard` is translated) to also handle the new
  guard, adding `Builders<T>.Filter.Eq(camelCased(path), expected)`.
- Integration test mirroring `ConditionalUpdateTests`: two parallel updates
  on same entity, first succeeds, second is silently dropped because the
  guard fails. Stale-write detection observable via
  `ReplaceOneResult.MatchedCount == 0`.

### PR 2 — `octo-mcp-service`

- `RuntimeEntityCrudTools.UpdateEntity`:
  - New optional `expected_version: ulong?` parameter.
  - When set, after the existing GetRtEntityByRtIdAsync read, build
    `AttributeEqualsGuard("RtVersion", expected_version)` and call
    `CreateConditionalUpdate`.
  - On guard failure (driver returns MatchedCount=0), re-read current
    entity, project payload, return structured
    `ConflictError { current_version: <new>, current_payload: <projected> }`
    via the existing `UpdateEntityResponse.ErrorCode = "VERSION_CONFLICT"`
    (new discriminator next to the current plain message).
  - When not set: existing behaviour, no guard, no version check.
- `RuntimeEntityCrudTools.DeleteEntity`: same shape — `expected_version` +
  `ConflictError`.
- `RuntimeEntityReadTools.GetEntityById` / `QueryEntities`: include
  `rt_version` in the response payload (the field is already on the wire as
  part of `RtEntity`; just surface it explicitly in the MCP DTO).
- Integration test: two simulated workers call `update_entity` against the
  same rtId; first succeeds with new version N+1, second hits
  `VERSION_CONFLICT` with `current_version: N+1`. Existing tests without
  `expected_version` continue to pass.
- Document the locking semantics in the repo's `CLAUDE.md` §8 (next to the
  risk-classification doc Phase A added).

### Estimated effort

- Engine PR: ~½ day (one record, one overload, one filter mapping, one
  integration test).
- MCP PR: ~1 day (two tool changes, response DTO discriminator, one
  integration test, CLAUDE.md update).
- Concept doc ADR-17 status flip: 15 min.

## 5. Open questions

1. **Guard naming.** `AttributeEqualsGuard` is generic and matches the
   `AttributeNewerThanGuard` precedent. A typed `RtVersionGuard(ulong)`
   would be cleaner ergonomically and harder to misuse, at the cost of
   single-purpose vocabulary in the engine. Engine maintainers' call;
   recommend `AttributeEqualsGuard` for symmetry, with a static
   `Guards.RtVersion(ulong)` helper.
2. **Backward-compat error code.** Add `ConflictError` as a new discriminator
   in `UpdateEntityResponse` _without_ removing the existing
   `IsSuccess/ErrorMessage` shape, so existing MCP callers that don't pass
   `expected_version` see no change in their happy or failure paths. The new
   field is `ConflictError? Conflict { current_version, current_payload }`,
   populated only when the guard fired.
3. **Delete-then-conflict.** If `delete_entity` is called with
   `expected_version` and the entity was already deleted, the guard naturally
   matches zero rows and we'd report VERSION_CONFLICT. A separate
   ENTITY_NOT_FOUND discriminator is cleaner. Cheap to disambiguate by
   re-querying when MatchedCount=0 (already in the proposed flow).
4. **Read-modify-write convenience.** Workers will be calling
   `get_entity_by_id` → `update_entity(expected_version=...)` constantly.
   Worth considering a single `replace_entity` tool that reads, applies a
   patch, and writes inside one MCP call — but that's Phase 2 of #4128, not
   the spike's scope.

## 6. Effect on ADR-17

ADR-17 in the concept doc currently says "Optimistic locking via
content-hash version" or similar. After this spike: switch to "Optimistic
locking via the existing `RtEntity.RtVersion` infrastructure counter, gated
by a new `AttributeEqualsGuard` in the engine's conditional-update
mechanism." Cross-link this doc.
