---
description: File transfer channel with session binding, platform file tools, CK and runtime model import formats, validate_ck_model, service-managed models.
applies_to: **/Routing/**, **/Services/*FileTransferStore.cs, **/Services/{EndTrackingStream,DeliveryTrackingResponseStream,FileTransferHandout,CkDraftInlinePackager}.cs, **/Tools/{FileTransfer,PlatformFile,CkModel*}Tools.cs
---

# File transfer and model import

## File I/O Architecture

Tools that need to receive or produce files use a separate HTTP channel: the JSON-RPC tool call coordinates an opaque transfer id and a one-time transfer key, and the actual bytes flow through `FileTransferController` at `/file-transfer/upload` and `/file-transfer/download`, with the id in the `X-Transfer-Id` request header and either the key in the `X-Transfer-Key` header or the bearer token of the MCP connection (`Routing/FileTransferRoutes.cs`).

### Components

- `IFileTransferStore` / `FileTransferStore` — in-memory + disk-backed buffers. Reservations live in `_pending`; completed uploads in `_uploads`; pending downloads in `_downloads`; the hashes of the live transfer keys in `_keys`. Files land in `Path.GetTempPath()/octo-mcp-file-transfer/<random>/`.
- `FileTransferHandout` — the one way a tool hands out a transfer: `ReserveUpload` / `RegisterDownload` register it for the caller and issue its key, and refuse a caller that cannot own a transfer (see *Security*).
- `FileTransferSweeper` — `BackgroundService` that purges expired entries + their files every 5 min.
- `FileTransferController` — `PUT /file-transfer/upload` claims the reserved slot named by the `X-Transfer-Id` header and writes the body to its path (5 GiB cap, streaming chunked). `GET /file-transfer/download` streams the file named by the header with range support and is single-use (deleted after the first complete GET). Both accept the creator's bearer token or the transfer key (see *Security*).
- `JobPollingHelper` — generic async-job poller for asset + bot service jobs (Succeeded/Failed/Timeout).

### Upload-then-import flow

```
prepare_file_upload(fileName) → { transferId, uploadUrlPath: "/file-transfer/upload", transferIdHeader: "X-Transfer-Id",
                                  transferKey, transferKeyHeader: "X-Transfer-Key", transferKeyExpiresAtUtc }
HTTP PUT to <publicUrl>/file-transfer/upload   (headers: X-Transfer-Id, X-Transfer-Key or Authorization: Bearer, Content-Type)
import_ck_model(transferId, tenantId) → waits for job, returns jobId
```

`prepare_file_upload` stores the owner with `McpSessionContext.GetFileTransferOwner(server)` (session label,
principal and tenant) through `FileTransferHandout.ReserveUpload`. Inside the import tool:
`store.GetUpload(transferId, McpSessionContext.GetFileTransferOwner(server))` returns the on-disk path (only for the
session and principal that reserved the upload, see *Security*); pass that to the SDK call (which requires a file path
argument, e.g. `ImportCkModelAsync(tenantId, filePath)`). On success, `store.DeleteUpload(transferId)` to clean up.

### Export-then-download flow

```
export_runtime_model_by_query(queryId) → starts asset job → polls → bot downloads to temp file → FileTransferHandout.RegisterDownload(server, store, ...) → returns { transferId, downloadUrlPath: "/file-transfer/download", transferIdHeader, transferKey, transferKeyHeader, transferKeyExpiresAtUtc }
HTTP GET <publicUrl>/file-transfer/download   (headers: X-Transfer-Id, X-Transfer-Key or Authorization: Bearer)
```

### With curl

An MCP client that does not hold the raw access token of its connection (for example one that signs in through the
MCP OAuth flow of its host) uses the one-time key of the tool result. The tool result names id and key; the values
below are placeholders:

```bash
# Upload: transferId and transferKey from prepare_file_upload, within the key's lifetime (default 5 minutes)
curl -X PUT "<publicUrl>/file-transfer/upload" \
  -H "X-Transfer-Id: <transferId>" \
  -H "X-Transfer-Key: <transferKey>" \
  -H "Content-Type: application/octet-stream" \
  --data-binary @model.yaml

# Download: transferId and transferKey from the download-producing tool (dump_tenant, export_*, download_file, ...)
curl -o dump.tar.gz "<publicUrl>/file-transfer/download" \
  -H "X-Transfer-Id: <transferId>" \
  -H "X-Transfer-Key: <transferKey>"
```

The key works once. A client that holds the access token of its MCP connection may send
`-H "Authorization: Bearer <token>"` instead of the key, also after the key expired or was used (a resume of an
aborted download, for example). Never put id or key into the URL or a log; a shell history keeps both, so prefer
reading them from a variable the tool output filled.

### Security

Invariant: an upload or download succeeds only for a request that carries either a valid bearer token whose principal
is the principal that created the transfer, or the transfer's valid one-time key; neither the transfer id nor the key
ever appears in a URL, log record, span attribute or error body.

- **Bearer token, same policy as MCP.** `FileTransferController` is `[AllowAnonymous]` at the routing level, because a
  request may authenticate with the key instead; every action first evaluates the transport policy
  (`McpAuthorizationPolicy.PolicyName`) on the token itself (`CheckCaller`). Without a key, no or an invalid token
  answers 401 and a token without the platform API scope 403, before the store is touched. Clients send the token of
  their MCP connection (`Authorization: Bearer <token>`). The check runs in the service, not only at a gateway,
  because only the service can bind the request to the creator.
- **One-time transfer key.** Every tool that creates a transfer (`prepare_file_upload` and every download-producing
  tool, all through `FileTransferHandout`) returns `transferKey`, `transferKeyHeader` (`X-Transfer-Key`) and
  `transferKeyExpiresAtUtc`. The key is 256 random bits (base64url), opaque, issued by
  `FileTransferStore.IssueTransferKey` and bound to the transfer, its direction (an upload key does not open a
  download and the other way round), the creator's principal and tenant. The store keeps only its SHA-256 hash and
  compares in fixed time. It lives `Mcp:FileTransferKeyLifetime` (default `00:05:00`; greater than zero and at most
  10 minutes, checked at start by `ValidateMcpServiceOptions`, and never longer than its transfer), works **once**:
  check and spend are one step under a lock (`TryClaimUploadWithKey`, `ConsumeDownloadKey`), so of two concurrent
  requests with the same key exactly one gets through. A request with a valid token of another principal or tenant is
  refused even with the right key. A key that is missing, wrong, expired, already used, foreign (of another transfer)
  or for the other direction, or presented with a foreign token, gets **401 with one identical body**, and the owner's
  live key stays unused (only an expired key is dropped). After an accepted request that then fails or is aborted
  (an aborted PUT, an aborted GET) the key stays spent: the client asks the tool for a new transfer, or resumes a
  download with the creator's token. The creator's token wins over the key: with a valid token of
  the creator a key sent along is ignored and not spent. A new key for the same transfer replaces the earlier one; a
  completed, aborted, cancelled, deleted or swept transfer takes its key with it. The tool `Message` names the header,
  never the key.
- **Client-credentials token without `tenant_id`: refused (fail closed).** Such a token has no
  `TransferOwnerKey` (`FileTransferOwner.CanOwnTransfer` is false): the tools refuse to create a transfer for it
  (`FileTransferHandout.OwnerRequiredError`, a download's temp file is deleted), and the endpoints never match it,
  neither with a bearer token nor with someone's key: both answer 401, nothing is written or served.
- **Bound to the creator's principal (HTTP side).** Every reservation and download stores
  `FileTransferOwner(SessionLabel, Principal)` (`McpSessionContext.GetFileTransferOwner`). The endpoints compare the
  caller's `McpCallerPrincipal.TransferOwnerKey` (`u:<tenant>:<sub>` for a user, the same as its `StoreKeyPrefix`;
  `c:<client_id>:<tenant>` for a client-credentials token with `tenant_id`) with the stored principal, ordinal
  (`FileTransferStore.IsPrincipalOwner`). Another user, the same subject in another tenant, another client id, or the
  same client id with a token of another tenant (a mirrored client) gets **404**, exactly like an unknown or expired id, and the entry stays for its
  owner. Fails closed: a missing principal on either side (a creator or caller without subject and client id) or the
  shared `anonymous` label never matches; a token without an owner key is refused with 401 like a missing token. The `Mcp-Session-Id` header plays no part on the HTTP side (a plain PUT/GET
  does not carry it); it is no identity there.
- **Session binding (tool side).** The tools pass `McpSessionContext.GetFileTransferOwner(server)` and the store
  compares both parts: the session label (`GetCallerLabel`: the validated principal's store key, plus
  `|<Mcp-Session-Id>` when the client sends that header) and the owner key (`TransferOwnerKey`). The label alone
  carries no tenant for a client, so the same client id with a token of another tenant would match it; the owner key
  refuses that caller. `GetUpload(transferId, caller)` returns the upload **only for that owner**; every consuming tool
  (`import_ck_model`, `import_runtime_model`, `restore_tenant`, `import_archive_data`, `upload_file`, `validate_ck_model`)
  passes the caller's label and a foreign id gets exactly the answer an unknown/expired id gets (`Upload '<id>' not
  found or expired.`). `cancel_file_transfer` goes through `CancelTransfer(id, caller)` (same two checks): foreign id = silent no-op
  with the usual success answer. The `anonymous` label never matches. New tools that consume an upload must pass the
  caller label; never add a `GetUpload` overload without the owner tag.
- **Id out of the URL.** The id travels only in the `X-Transfer-Id` header: no route carries it in the path, the query
  is not read (an id there is refused with 400 because the header is missing), and the old routes
  `/file-transfer/{upload,download}/{id}` answer 404. Access logs, `url.path`, `url.full`, `url.query` and `http.route`
  therefore hold only the fixed paths. The ASP.NET Core instrumentation records no request headers by default; a
  deployment that turns on request-header capture must not list `X-Transfer-Id`. Error responses and log lines of the
  controller never name the id or the key (the success answer of a PUT returns the id to its owner, as before). The
  same holds for `X-Transfer-Key`: no header capture may list it. Tool results return `uploadUrlPath` /
  `downloadUrlPath` without the id plus `transferIdHeader` and the id in `transferId`.
- **One PUT per upload.** The first PUT claims the slot atomically (`TryClaimUpload`, compare-and-swap on the pending
  entry); a concurrent second PUT of the owner gets **409** (a foreign caller still gets 404, so 409 tells nothing
  about someone else's id). With the key, the loser of two concurrent PUTs gets 401 when the winner already spent the
  key, or 409 while the winner holds the slot (its key then stays unused). A PUT that fails or is aborted (client gone, I/O error, 5 GiB cap exceeded) **ends the
  reservation** and deletes the partial file (`AbortUpload`): the client needs a new `prepare_file_upload`. A PUT after
  a completed upload answers 404. A PUT still needs a `Content-Type` header (framework behaviour).
- **Single-use download.** `GET /file-transfer/download` deletes the entry **and** the temp file once the last
  byte was served and the client did not abort (`EndTrackingStream` + `Response.OnCompleted`). "Did not abort" is read
  from two signals: `RequestAborted` and `DeliveryTrackingResponseStream`, which writes the body to the response pipe
  and marks the delivery lost when a write or flush reports the connection completed. The second signal is needed
  because Kestrel discards writes after an abort without an error and cancels `RequestAborted` on the thread pool, so
  under load the copy could read the file to its end and the token could come too late. A second GET is 404; a
  download fetched with the key spends the key on the first GET, so a resume after an aborted GET needs the
  creator's token. An aborted transfer, or a range that stops before the end of the file,
  keeps the entry so the owner can resume with a range request; the entry is then deleted when the resume reaches
  the end, or by the TTL sweeper at the latest. Parallel chunked downloads: the chunk that reaches the end deletes
  the file for the others — download sequentially.
- **Expiry.** Reservations and downloads live 30 minutes from creation; an expired id answers 404.
- **Known limitation:** no per-caller disk quota or limit of concurrent reservations; with the token required this is
  no longer open to anonymous callers.

Tests: `Routing/FileTransferControllerTests` (one test per abuse case, against Kestrel with the real JWT bearer handler,
the transport policy and the OpenTelemetry ASP.NET Core instrumentation; for the key: no token, expired, reused,
another transfer, another user, tenant and client, wrong direction, concurrent use, one identical refusal body, no key
in spans, logs or bodies; a client token without `tenant_id`), `Services/FileTransferStoreTests` (key entropy,
lifetime and cap, binding, single use under concurrency, clean-up), `Services/DeliveryTrackingResponseStreamTests`,
`Tools/FileTransferToolsTests` (key in the tool result, tenantless client refused), `Tools/FileTransferOwnershipTests`,
`Configuration/ValidateMcpServiceOptionsTests` (key lifetime).

**Do not** add base64-in-tool-parameter as an alternative path — the file-transfer endpoints are the only sanctioned mechanism for binary payloads.

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
- **Zip of the source `ConstructionKit/` folder** (a zip that holds `ckModel.yaml`, which decides it): the asset repository's
  `ModelsController.ImportCk` compiles it on the path of `validate_ck_model` (same extraction limits, same compiler) and queues
  the compiled model; the bot import job still reads compiled models only. Compile errors fail the call (`400`, code
  `CK_MODEL_COMPILE_FAILED`, the findings `validate_ck_model` reports) before anything is queued. Needs an asset repository with
  source-zip support in `ImportCk`; an older one reads a file of the zip as a compiled model and fails.
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
