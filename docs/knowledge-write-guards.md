### Knowledge types are written only by the knowledge tools (AB#8568)

`Services/KnowledgeWriteRefusal.cs` keeps the generic write tools off the System.Ai knowledge types
(`KnowledgePage`, `KnowledgePageVersion`, and any type whose `GetBaseTypes(true)` contains one of them): a generic write
would skip the content guard (AB#8475) and the `KnowledgePageVersion` with author and time (AB#8484). Same contract shape
as the generic-create refusal: `ErrorCode = GENERIC_WRITE_REPLACED_BY_KNOWLEDGE_TOOL`, `ErrorParameters` = `ckTypeId`
(runtime id) and `tool` (the knowledge tools to use, comma-separated, or `none` for a delete); the message carries ids
only. Where it runs:

- `CreateCoreAsync` (create_entity, create_entity_with_secrets): after type resolution, before the generic-create
  refusal, secret handling or any session.
- `UpdateCoreAsync` (update_entity, set_entity_secrets) and `delete_entity`: on the requested type before the session is
  opened, and again on `existingEntity.CkTypeId` inside the transaction (a base type such as `System/Entity` can name a
  page), which aborts the transaction.
- `import_runtime_model`: `RunImport` takes a `precheck` that runs after the target tenant is resolved and before the file
  is sent: `CkModelFileTools.KnowledgeImportPrecheckAsync` -> `Services/KnowledgeImportCheck.cs`. Check and import read
  the same private snapshot (`CheckAndImportSnapshotAsync`: a fresh per-import directory
  `octo-mcp-import-<random>` from `PrivateTempDirectory.Create` - `Directory.CreateTempSubdirectory`, atomic, mode 0700,
  never a shared fixed path in `/tmp`, so created and owned by the process user by construction; on Unix verified after
  creation with managed APIs only (`File.GetUnixFileMode` without group/other bits, `LinkTarget == null`; no `lstat`
  P/Invoke - fragile `struct stat` offsets, glibc < 2.33 has no `lstat` export), else refused with `ImportCheckFailedMessage`; file `snapshot<ext>` mode 0600; the whole
  directory deleted in `finally`), never the upload slot: a PUT that started before the
  upload completed can still write the slot (CWE-367). A copy that fails is refused with `ImportCheckFailedMessage`. It judges the **decoded
  ids**, never the raw text (a text scan was bypassed by `System.Ai\/KnowledgePage`, `\u002F`, YAML escapes or casing):
  - format by the upload's extension, case-insensitive: `.zip`, `.json`, `.yaml` and `.yml` (both YAML). Any other
    extension is refused with `ImportCheckFailedMessage` (fail closed; the check does not rely on the SDK or the bot
    refusing it - the bot reads a file of unknown content type as YAML). In a zip every `.json`/`.yaml`/`.yml` entry is
    checked, streamed and capped at 5 GiB decompressed per entry (`MaxEntryBytes`, zip bombs); other entries are
    skipped, because the bot (`ImportModelJob.GetTempFile`) picks its entry only by the extension `.json` or `.yaml` and
    never reads another one (a README in the zip does not refuse it);
  - JSON: the importer's own streaming reader (`IRtJsonSerializer.DeserializeStreamAsync` + `ReadBulksAsync`, bulks of
    5000; Newtonsoft, so key casing and escapes resolve exactly as in the import; a file without the leading `$schema`
    fails there as in the import);
  - YAML: YamlDotNet `Parser` events (the library the importer's deserializer uses), no document in memory. The importer's
    YAML reader is not used because its schema validation copies the whole file into memory. Scalars are decoded by the
    parser; aliases of scalars are resolved (also as keys); keys are compared case-insensitively; bounded against
    hostile input: more than 64 nested containers (`MaxYamlDepth`), 10,000 anchored scalars (`MaxYamlAnchors`) or
    16 MiB of retained anchored values (`MaxYamlAnchorBytes`, UTF-16) refuse the file, also inside a zip;
  - per entity: `ckTypeId` and each association's `targetCkTypeId` -> protected type (runtime or full id, any casing,
    version suffixes stripped), a type the CK cache knows that derives from one (`KnowledgeWriteRefusal.IsKnowledgeType`,
    the same rule as the CRUD tools), or - in a tenant that has `KnowledgePage` - a type the cache does not know (it
    cannot be ruled out as derived; such an import would fail anyway); `roleId` -> `LinksTo`, `GeneratedFrom`,
    `VersionOf`. YAML: any other scalar that is itself a protected id refuses too (superset); free text that only mentions
    an id does not;
  - fail closed: any exception (corrupt/encrypted zip, unsupported compression, parse error, missing file, I/O) refuses
    with the generic `KnowledgeWriteRefusal.ImportCheckFailedMessage`; the exception goes to the server log only (no
    `ex.Message`, it can carry server paths). The import response carries `ErrorCode` and the message only
    (`JobStartedResponse` has no `ErrorParameters`). The upload buffer stays (expires on its own).
  - Defence in depth that is still missing: the asset repository does not refuse knowledge types on `ImportRt` itself
    (octo-cli and GraphQL callers are not covered by this MCP check; a server-side preflight like
    `RtIdentityOwnedWriteCheck` in the engine would close that).

**Delete is ruled out over MCP** (CPO decision 2026-11-08): deleting a knowledge page is not a knowledge operation in M1.
Agents set `Rejected` with `knowledge_set_status`; a person deletes pages outside MCP (GraphQL or octo-cli). There is no
generic association write tool, so the knowledge associations are reachable only through the import (refused) and the
knowledge tools. Reads (`get_entity_by_id`, `query_entities*`, `navigate_associations`, …) are unchanged.
Tests: `Tools/KnowledgeGenericWriteRefusalTests` (incl. `KnowledgeRuntimeImportRefusalTests`).

### Lint reports are written only by `knowledge_write_lint_report` (AB#8488)

`Tools/KnowledgeLintTools.cs` is the write channel of the nightly knowledge lint (System.Ai.Knowledge blueprint, pipeline
`knowledge-lint`, `McpToolCall@1` as service account `knowledge-linter`). One fixed type (`System.Ai/KnowledgeLintReport`),
one insert per call, nothing updated. Rules a later change must keep:

- **Content guard over every text** (name, `runRef`, each finding's `pageRef`, `relatedPageRef`, `detail`): a finding can
  quote a SourceArtifact, which is unguarded ingest text. Fails closed without a guard, like the page tools.
- **`IsValidated` is always written `false`.** The lint's flag precision is measured on the Control Set first (AB#8488
  AC2); a person sets the flag outside MCP once it is. There is no parameter for it on purpose.
- **No page is touched** (no status, never `Verified`). Finding refs must be page slugs (`KnowledgePages.IsValidSlug`),
  which also keeps free text out of the ref fields.
- Validation (kind by name or key, slugs, detail <= 2000, <= 500 findings, run data via `KnowledgeTools.ValidateRunData`,
  `completedAt` at most 5 minutes in the future, `verifiedThrough` not after `completedAt` and, without it, not after
  now - it is the next run's cursor) runs before the session. A tenant whose report type has no `IsValidated` (System.Ai before AB#8488) gets a clear error.
- `KnowledgeWriteRefusal` lists the report type, with its own message and `tool` = `knowledge_write_lint_report`
  (delete: `none`); a type derived from it is refused with `tool` = `none` (the tool writes only the base type), also
  on import. `KnowledgeImportCheck.ProtectedTypeNames` lists `KnowledgeLintReport` next to the page types.
- The guard call is a small copy of `KnowledgeTools.GuardAsync`; a change to one belongs in the other.

Tests: `Tools/KnowledgeLintToolsTests` (incl. `KnowledgeLintReportGenericWriteRefusalTests`).

### Platform updates of Blueprint pages (AB#8780)

`Tools/KnowledgePlatformUpdateTools.cs` + `Services/Knowledge/KnowledgePlatformUpdates.cs` complete the blueprint update
rule of AB#8778 for knowledge pages (the engine never refreshes an existing page; only new version entities arrive).
Rules a later change must keep:

- **The rule lives in `KnowledgePlatformUpdates.Decide`**, used by list, adopt and (through list) the lint. A Blueprint
  version needs both the author prefix `blueprint:` and the `RtBlueprintSource` stamp; the knowledge tools never write
  such an author. A tenant version that shares the current number with a Blueprint version makes the page *edited* (safe
  direction).
- **Edited pages: ancestor chain, never numbers or arrival times** (reopen 2026-11-13, kb-test2: an edit written on `@1`
  after `@2` arrived hid `@2`). `Ancestors` follows `BaseVersion` (System.Ai 4.11.0, written by
  `BuildWrite(recordBase: ctx.RecordsBase)`) and `RestoredFromVersion` from the current version, plus `@1` as root; a
  Blueprint version in the chain supersedes all Blueprint versions up to its number; every Blueprint version above that
  (and above `DismissedBlueprintVersion`) is pending, the newest is reported. A tenant version without `BaseVersion`
  (written before 4.11.0) ends its chain - the safe direction (a finding a person adopts or dismisses), never a silent
  loss. `FindAsync` reads the chains step by step in batches; a chain longer than `MaxChainRounds` sets `Truncated`.
  octo-cli `SetKnowledgePageStatus` writes versions without `BaseVersion` (a person's verify then ends the chain - a
  finding, not a loss).
- **The adopt writes no version** and copies the status of the seeded version, so the page stays "unedited" for the next
  blueprint update. It only ever adopts for a page whose current version is a Blueprint version; there is no parameter to
  pick a page or a status. Never set Verified on anything else (AB#8565).
- `LinksTo` follows `DeclaredLinks` only when the version has the attribute; unknown slugs are reported, not created.
- Writes open the session with `write: true` (channel `System.Ai/KnowledgeTools`, AB#8575) and go through
  `KnowledgeTools.ApplyAsync` (version re-check in the transaction) and `KnowledgeTools.GuardAsync`.
- `BuildWrite` takes `highestVersion` (`KnowledgePlatformUpdates.HighestVersionAsync`): a new version gets the next free
  number, never `CurrentVersion + 1` blindly.

Tests: `Tools/KnowledgePlatformUpdateToolsTests` (fixtures unedited, edited, deleted, link change, already current; adopt
by restore, dismiss, audit, Verified; edit after a newer Blueprint version arrived, chain via restore, legacy versions
without `BaseVersion`, missing chain link).
