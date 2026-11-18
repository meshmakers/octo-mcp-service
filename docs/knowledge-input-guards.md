---
description: Content guard of knowledge writes (detectors, tenant secret fingerprints, fail closed) and the SSRF policy of knowledge-source URL fetches.
applies_to: src/McpServices.ContentGuard/**, **/Services/ContentGuard/**, **/Services/KnowledgeUrl*.cs, **/KnowledgeFetchOptions.cs
---

# Knowledge input guards

## Outbound fetches: the knowledge-source URL policy

`KnowledgeResources` materialises `AiKnowledgeSource` entities. The `Url` kind used to be fetched with
a plain `httpClientFactory.CreateClient("knowledge-fetch").GetAsync(storedUrl)` — no scheme check, no
host check, unbounded body, auto-following redirects. The URL is **tenant-writable** data and the
request leaves from **inside the cluster**, with the response body handed back to the caller: that is
a full SSRF primitive (cloud metadata endpoints, cluster-internal services, exotic schemes).

The fetch now goes through `Services/KnowledgeUrlFetcher` (`IKnowledgeUrlFetcher`) and
`Services/KnowledgeUrlPolicy`, configured by `Options/KnowledgeFetchOptions` (section
`KnowledgeFetch`, env `OCTO_KNOWLEDGEFETCH__…`):

- **Scheme allow-list** — `http` / `https` only. (Note: on Unix an absolute *path* like `/etc/passwd`
  parses as an absolute `file://` URI, so it is caught here rather than by the absolute-URL check.)
- **Address ranges blocked after DNS resolution**, not before. Checking the literal host is useless:
  any name — including one the tenant controls — can point at `169.254.169.254`. `IHostAddressResolver`
  (prod: `SystemDnsResolver`) resolves the host and **every** returned address must be publicly
  routable; one blocked record in a multi-record answer refuses the whole fetch, because which record
  the connection would pick is not ours to decide. Blocked: loopback, `0/8`, RFC 1918, RFC 6598 CGNAT,
  link-local incl. the IPv4/IPv6 metadata endpoints, multicast/broadcast, IPv6 loopback / link-local /
  unique-local, and the IPv4-mapped + IPv4-compatible IPv6 forms of all of them. An IP literal skips
  DNS but not the range check. An unresolvable host is a refusal (fail closed).
- **Size limit** — `MaxResponseBytes` (default 1 MiB). A declared `Content-Length` over budget is
  refused before streaming; an undeclared oversized body is read to the budget and reported as
  `Truncated` (a partial CLAUDE.md fragment is still useful, so truncation is not an error).
- **Time limit** — `TimeoutSeconds` (default 10) as **one** budget for the whole exchange including
  redirects. The named `knowledge-fetch` client is registered with `Timeout.InfiniteTimeSpan` on
  purpose: a per-request timeout would multiply with the hop count.
- **Redirects are followed by hand**, up to `MaxRedirects` (default 3), with the primary handler at
  `AllowAutoRedirect = false`. Auto-redirect would connect to the target before anything could inspect
  it — exactly the hole the policy exists to close. Every `Location` is re-validated against the full
  policy, so a public host cannot bounce the request into the cluster.
- **Operator escape hatches** — `AllowedHosts` (exact, or a leading-dot suffix entry like
  `.internal.example.com`) waives **only** the address-range check for named internal sources; scheme,
  size, time and the per-hop redirect re-check still apply, and a redirect target must be allow-listed
  in its own right. `AllowPrivateNetworks` disables the address check wholesale for operators who
  terminate egress in a proxy. Both default to off/empty.

**There is no unguarded fallback.** `KnowledgeResources` resolves `IKnowledgeUrlFetcher` with
`GetService`; when it is not registered the source renders as "fetcher is not configured", it is never
fetched raw. Don't reintroduce a direct `HttpClient` call on this path.

## Content guard of knowledge writes

`src/McpServices.ContentGuard` (library without OctoMesh dependencies, registered as `IContentGuard` in `Program.cs`) checks
the text of a write before it is stored. Every knowledge page write calls it; the memory writes of the agent
runtime are meant to use the same library (move it into a shared package then). Concept: `octo-ai-services/docs/concepts/tenant-builder-agent-concept.md` §3.9.

| Detector | Rule (`ContentDetectors`) |
|---|---|
| `AnthropicKey` | `sk-ant-` + key type + dash (`sk-ant-api03-`, `sk-ant-admin01-`). A bare mention of `sk-ant-` passes. |
| `PemBlock` | `-----BEGIN …-----`, a body of 16+ base64 characters and `-----END …-----`. A mention of the header alone passes. |
| `Jwt` | `eyJ….eyJ….…` (three base64url parts). |
| `HighEntropyToken` | a run of 32+ base64/base64url characters (`=` only as padding) that mixes upper case, lower case and digits, is not hex, has an entropy of at least 4.2 bits per character, switches character class on at least 30 % of the positions and is covered by runs of four or more lower-case letters to less than 30 %. Never scanned: the scheme/host/path of URLs (share-link ids) and Teams thread ids (`…@thread.`); URL query strings are scanned. A heuristic: it misses about 3 % of random keys and must not be weakened for convenience. |
| `GitHubToken` | `ghp_` / `gho_` / `ghu_` / `ghs_` / `ghr_` + 16 or more letters/digits, or `github_pat_` + 22 or more. A bare prefix passes. |
| `AwsAccessKey` | `AKIA` or `ASIA` + exactly 16 upper-case letters/digits. |
| `CredentialAssignment` | a key ending in `password`, `passwd`, `secret`, `token`, `api_key`/`apikey`, `access_key` or `private_key` (any prefix, any case: `DB_PASSWORD`, `client_secret`, `"clientSecret"`), then `=` or `:` (not `==`, `=>`, `:=`) and a literal value of 4+ characters. A quoted value counts unless it has whitespace; an unquoted value right after `=` (`password=…`, connection strings, env files) always counts; an unquoted value after `:` or a spaced `=` (YAML, code, prose) counts only with a digit or symbol, and member chains (`options.Token`), calls, `[`-indexing and lower-case kebab names (`existingSecret: octo-mongodb`) pass. Placeholders pass only as whole reference syntaxes (a single leading `#`, `@`, `&`, `.`, `?` is not one): `$NAME`, `$env:NAME`, `${…}`, `$(…)`, `{{ … }}`, `#{…}`, `{name}`, `%NAME%`, `<…>`, `[REDACTED]`, `***`, `...`, repeated characters, type names (`String!`), `null`/`true`/`string`/`redacted` and similar, `your…`/`example…`. |
| `TenantSecret` | a word of the text (or the value of a `key=value` pair, or the whole text) whose SHA-256 equals the fingerprint of a Secret attribute value of the tenant. Values shorter than 8 characters are not compared. |

Rules to keep:

- **Never log or return the content.** A refusal (`ContentGuardResult.Message`, error code `ContentRejected`) and the
  Warning log line carry tenant, caller, operation, detectors and field names only. Once ActionRecords exist
  the caller records the refusal there with status `Refused`.
- **Tenant secrets only as fingerprints.** `Services/ContentGuard/TenantSecretFingerprints.ReadAsync` reads the top-level
  `Secret` attributes of every entity of every non-abstract type (paged, `PageSize` 2000, no cap: a cap would let a
  secret beyond it pass) **with the caller's session** (what the caller could reveal), reveals
  them through the engine's `ISecretAttributeProtector` (service name `mcp-content-guard` in the decrypt metric) and
  hands only SHA-256 fingerprints to the guard. Nothing is cached. Without a configured key ring
  (`SecretEncryption:*`) the set is empty and the detector is off; the other detectors still run. Secret members inside
  records are not read yet.
- **Fail closed.** When the fingerprint read throws, the write is refused (`TenantSecret` on every non-empty field).
- **False positives**: `ContentGuardTests.KnowledgeTextOfThePlatform_IsAccepted` pins the shapes our knowledge is full
  of (ids, hashes, GUIDs, paths, branch names, URLs, env variable names, documentation naming a prefix).
  `Corpus_FromEnvironment_PassesTheGuard` runs the guard over real files when `OCTO_CONTENT_GUARD_CORPUS` lists
  directories (skipped in CI), e.g. the octo-claude-skills `skills` folder and a Claude Code memory folder.
  `ProseAndPlaceholdersAboutCredentials_AreAccepted` pins prose that names a credential without a value,
  placeholders, type names and code. Test fixtures use only obvious fakes (`FAKEfake0000…`, `hunter2fake`), never real values.
