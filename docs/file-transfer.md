---
description: File transfer channel with session binding, platform file tools, CK and runtime model import formats, validate_ck_model, service-managed models.
applies_to: **/Routing/**, **/Services/*FileTransferStore.cs, **/Services/{EndTrackingStream,CkDraftInlinePackager}.cs, **/Tools/{FileTransfer,PlatformFile,CkModel*}Tools.cs
---

# File transfer and model import

## File I/O Architecture

Tools that need to receive or produce files use a separate HTTP channel: the JSON-RPC tool call coordinates an opaque transfer id, and the actual bytes flow through `FileTransferController` at `/file-transfer/{upload,download}/{id}`.

### Components

- `IFileTransferStore` / `FileTransferStore` — in-memory + disk-backed buffers. Reservations live in `_pending`; completed uploads in `_uploads`; pending downloads in `_downloads`. Files land in `Path.GetTempPath()/octo-mcp-file-transfer/<random>/`.
- `FileTransferSweeper` — `BackgroundService` that purges expired entries + their files every 5 min.
- `FileTransferController` — `PUT /file-transfer/upload/{id}` writes the body to the reserved path (5 GiB cap, streaming chunked). `GET /file-transfer/download/{id}` streams the file with range support and is single-use (deleted after the first complete GET).
- `JobPollingHelper` — generic async-job poller for asset + bot service jobs (Succeeded/Failed/Timeout).

### Upload-then-import flow

```
prepare_file_upload(fileName) → { transferId, uploadUrlPath }
HTTP PUT to <publicUrl>/file-transfer/upload/{transferId}
import_ck_model(transferId, tenantId) → waits for job, returns jobId
```

Inside the import tool: `store.GetUpload(transferId, McpSessionContext.GetCallerLabel(server))` returns the on-disk path (only for the session that reserved the upload, see *Security*); pass that to the SDK call (which requires a file path argument, e.g. `ImportCkModelAsync(tenantId, filePath)`). On success, `store.DeleteUpload(transferId)` to clean up.

### Export-then-download flow

```
export_runtime_model_by_query(queryId) → starts asset job → polls → bot downloads to temp file → store.RegisterDownload(...) → returns { transferId, downloadUrlPath }
HTTP GET <publicUrl>/file-transfer/download/{transferId}
```

### Security

The HTTP endpoints stay **anonymous** by design (curl-based clients must keep working): the random 128-bit
transfer id (`Guid.NewGuid().ToString("N")`, 30 min TTL) in the URL path is the capability. The rules below narrow what
that capability allows:

- **Session binding (tool side).** The store records the owner (`McpSessionContext.GetCallerLabel(server)`: the
  validated principal's store key, plus `|<Mcp-Session-Id>` when the client sends that header) for every
  reservation and download. `GetUpload(transferId, callerSessionId)` returns the upload **only for that owner**; every
  consuming tool (`import_ck_model`, `import_runtime_model`, `restore_tenant`, `import_archive_data`, `upload_file`)
  passes the caller's label and a foreign id gets exactly the answer an unknown/expired id gets (`Upload '<id>' not
  found or expired.`) — no information leak, the owner's file stays untouched. `cancel_file_transfer` goes through
  `CancelTransfer(id, caller)`: foreign id = silent no-op with the usual success answer. The shared `anonymous` label
  (`McpSessionContext.AnonymousCallerLabel`, unidentifiable caller) **never matches** — it fails closed.
  New tools that consume an upload must pass the caller label; never add a `GetUpload` overload without one.
- **Single-use download.** `GET /file-transfer/download/{id}` deletes the entry **and** the temp file once the last
  byte was served and the client did not abort (`EndTrackingStream` + `Response.OnCompleted`, checked against
  `RequestAborted`). A second GET is 404. An aborted transfer, or a range that stops before the end of the file,
  keeps the entry so the client can resume with a range request; the entry is then deleted when the resume reaches
  the end, or by the TTL sweeper at the latest. Parallel chunked downloads: the chunk that reaches the end deletes
  the file for the others — download sequentially.
- **Still open** (separate follow-ups): bearer token on `/file-transfer`, atomic claim of the upload slot
  (two parallel PUTs on one id), disk quota / limit of concurrent reservations. A PUT still needs a `Content-Type`
  header (framework behaviour, not a documented rule).

For stricter setups, put the service behind your own auth gateway. **Do not** add base64-in-tool-parameter as an alternative path — the file-transfer endpoints are the only sanctioned mechanism for binary payloads.

### Platform file system tools (`PlatformFileTools`)

`list_files`, `upload_file`, `download_file`, `create_folder`, `delete_file` wrap the typed
`IAssetServicesClient.Files` client of the SDK (System.Files — REST `{tenant}/v1/files` for bytes,
GraphQL for metadata). They are Family 1 (`AssetClientContext`) and use the file-transfer channel above for
bytes: `prepare_file_upload` → PUT → `upload_file(transferId, root, path)`; `download_file` stages the bytes in
`IFileTransferStore` and returns a `downloadUrlPath`. Files are addressed by root well-known name (default
`Files`) + `/`-separated path, or by `rtId`. Server error codes (NAME_CONFLICT, PATH_NOT_FOUND, …) are returned
in `ErrorCode`. Risk: `list_files`, `download_file`, `create_folder` Low; `upload_file` Medium (`conflict=replace`
overwrites content); `delete_file` **High** + `confirm=true` — deleting a folder is recursive server-side
(all or nothing, delete cap 2,000 entries), the refusal message reports what would be deleted. The tenant needs
no `enable_*` call; `GET {tenant}/v1/files/capabilities` reports `available:false / SYSTEM_FILES_MISSING` otherwise.

### CK + runtime model upload formats (gotchas)

`import_ck_model` and `import_runtime_model` are NOT JSON-only — confirmed accepted formats:

- **Single compiled YAML** from the CK MSBuild output at `bin/<config>/net10.0/octo-ck-libraries/<Project>/out/ck-<name>-<major>.yaml`. Easiest path after a `dotnet build`.
- **Single compiled JSON** in the same shape as files under `~/.octo/local-catalog/ck-models/v2/<letter>/<Model>/<major>/ck-<name>-<version>.json`.
- **Zip of a compiled model** (the first `.json`, else the first `.yaml` entry is read as the compiled model).
- **Not** a zip of the source `ConstructionKit/` folder: `ImportCk` does not compile (checked against the asset repository's
  `ModelsController.ImportCk` and the bot import job). Compile with `dotnet build` / `octo-ckc` first, or use
  `validate_ck_model`, which does compile a source zip.
- For runtime models: a single YAML/JSON conforming to `runtime-model.schema.json` (an `entities:` list keyed by `rtId` + `ckTypeId`), or a zip thereof.

The tool description says "PUT the file to the returned URL" — historically said "PUT the JSON/zip" which was misleading. Asset-services accepts all of the above; schema validation happens server-side after the file lands in the file-transfer store.

### validate_ck_model (`CkModelValidationTools`)

Validates a CK model draft against the tenant **without importing it**: `prepare_file_upload` → PUT → `validate_ck_model(transferId)`,
or inline for MCP-only clients: `validate_ck_model(files: [{name, content}])` with names relative to `ConstructionKit/`.
Exactly one of the two. Inline limit (`CkDraftInlinePackager`): 1 MiB of UTF-8 content and 2000 files; over it the call is refused
with `errorCode` `InlineDraftTooLarge` and an error with `messageNumber` 9005 naming the upload path. Deliberately no base64 zip
parameter (see the base64 rule in `AGENTS.md`): text files of a draft are not binary payloads, a zip is. Authentication
runs before the draft is staged; the draft becomes an owner-only temporary zip deleted in `finally`; nothing is kept per process
(guard test `ValidateCkModel_RepeatedInlineCalls_DoNotRetainTheDraft`).
Family 1 (`AssetClientContext`, SDK `IAssetServicesClient.ValidateCkModelAsync`, asset repository `POST {tenantId}/v1/Models/ValidateCk`,
read-only scope). Accepts a zip of the source `ConstructionKit/` folder (the folder holding `ckModel.yaml`; compiled on the server,
dependencies from the server's catalogs) or a compiled model (yaml, json or a zip of one). The compiled model then runs through the
import's own pre-write checks against the tenant (engine `ITenantContext.ValidateCkModelAsync`: CK language 91, `minEngineVersion` 126,
system reference / dependency resolution 8492 with `parameters.reason`) and, without errors, the tenant upgrade guard (`isBlocked`,
`blockers`). Result: `isValid`, `modelId`, `sourceKind` (`Source`/`Compiled`), `errors`/`warnings` with `messageNumber`, `level`,
`file` (relative to `ConstructionKit/`, never a server path), `elementPath`, `message`, `parameters`. A missing dependency is an error
in the list, never a failed call. Risk **Low** (nothing is written). The upload is **kept** (unlike the import tools), so the same
upload can be validated again unchanged. A corrected draft needs a new `prepare_file_upload` and a new PUT: the first PUT
consumes the reservation, a second PUT on the same id answers 404. Tests: `CkModelValidationToolsTests`.

### Service-managed CK models — don't use import_ck_from_catalog

The CK library status flags every model as either user-managed or **service-managed** (`isServiceManaged: true`). Service-managed models include `System` (always), `System.Communication`, `System.StreamData`, `System.Reporting`, `System.UI`, `System.Ai`, `System.Bot`, `System.Identity`, `System.Notification` — anything that backs a backend service feature.

For service-managed models, `import_ck_from_catalog` will silently no-op even when the model is NOT loaded in the target tenant. The tool returns `IsSuccess=true` with a message like "Nothing to import — already up to date", but `get_available_models` will not list the model afterwards. Misleading but consistent.

The correct way to make those models available is the matching `enable_<feature>` tool:

| Service-managed model | Enable tool |
|---|---|
| `System.Communication-*` | `enable_communication` |
| `System.StreamData-*` | `enable_stream_data` |
| `System.Reporting-*` | `enable_reporting` |
| `System.UI-*` | (no MCP tool yet — install via Studio or octo-cli) |
| `System.Files-*` | none needed — imported into every tenant; the platform file system works without any `enable_*` tool (see above) |

For user-managed CK models (Basic.*, Industry.*, EnergyIQ, Loxone, custom tenant models), `import_ck_from_catalog` works correctly and DOES load them, and the response carries the job id of the batch import. The reliable verification is `get_ck_library_status` — it reports the actually-loaded version and `modelState=Available`. `get_available_models` may be stale right after an import.
