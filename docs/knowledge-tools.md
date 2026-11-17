## Knowledge base tools (AB#8484)

`Tools/KnowledgeTools.cs` (family 2, caller session) + `Services/Knowledge/KnowledgePages.cs` (model ids, content read/write,
markdown rendering) + `Resources/KnowledgePageResources.cs`. Types: System.Ai 4.7.0 (octo-ai-services AB#8483,
`src/SystemAiCkModel/README.md`); a tenant without them gets a clear error naming System.Ai 4.7.0.

- **Every page write goes through `KnowledgePages.BuildWrite`**: page insert/update plus a new `KnowledgePageVersion`
  (`rtWellKnownName` `<slug>@t<n>` - the tenant key namespace of AB#9103; `<slug>@<n>` is the blueprint's, read both with
  `KnowledgePages.VersionNames` / `FindVersionAsync`; full content, `Author` = `user:<sub>` or `client:<client_id>`, `AuthoredAt`, `ChangeNote`,
  run metadata) and the `VersionOf` association, in one transaction. The page's `CurrentVersion` points at it. A restore writes
  a new version with `RestoredFromVersion`; nothing is ever deleted. Links (`LinksTo`) are not content and make no version.
- **Write channel** (AB#8575): the write tools open their session with `OpenAsync(server, tenantId, write: true)`, which adds
  `RtSecurityContext.WithWriteChannel(KnowledgePages.WriteChannel)` = `System.Ai/KnowledgeTools`. System.Ai 4.8.0 declares
  `writeAccess { principals: [Interactive], channels: [System.Ai/KnowledgeTools] }` on `KnowledgePage` and
  `KnowledgePageVersion`, so the engine (AB#8574) admits these writes for every caller and refuses a client-credentials
  caller's direct GraphQL/REST/ImportRt writes with message 144. The constant must equal the model's channel (a mismatch
  breaks every knowledge write of a client caller); read tools never get the channel; the channel is never taken from
  caller input. Only these four tools may set it.
- **Content guard first** (`IContentGuard`, AB#8475): every tool that writes text (upsert, restore, set_status) checks title,
  summary, sections, citations (all four fields), flags, the slug and the free-text extras (`changeNote`, `promptName`, `modelId`, `runRef`) before anything
  is written; tenant secrets come in as fingerprints read with the caller's session. No guard registered = refusal.
  `knowledge_link` writes only an association between two existing, slug-validated pages and carries no text.
- **Status**: every content write (upsert, restore) sets `Draft`; `knowledge_set_status` moves Draft/InReview/Rejected and
  refuses `Verified` for **every** caller, user token included (`VerifiedNotAllowedOverMcp`, AB#8565, decision G2a on Epic
  AB#8449: MCP callers are always AI clients, also in a user's session). The check runs before the tenant is opened. A person
  sets `Verified` outside MCP with octo-cli `SetKnowledgePageStatus` (user login only; writes the page status and a
  `KnowledgePageVersion` in one GraphQL mutation) or GraphQL; later the Refinery Studio review view. No knowledge tool path
  leads to `Verified` any more (upsert and restore write `Draft`), and the generic tools are closed by AB#8568.
- **Content hash contract**: `ContentHash` = SHA-256 (lower-case hex) of `Render(content, slug, 0)`. octo-cli
  `SetKnowledgePageStatus` recomputes it with a port of `Render`; both repos pin the same golden hash
  (`Services/Knowledge/KnowledgeContentHashGoldenTests` here). Changing `Render` means changing the octo-cli port in the same step.
  `Render` ends every line with an explicit `\n` (never `AppendLine`/`Environment.NewLine`), so the hash is the same on Windows.
- **Update = omitted means unchanged** (AB#8567): on `knowledge_upsert_page` for an existing page, title and sections are
  required and replace; `kind`, `summary`, `citations`, `hallucinationFlags` keep the current value when omitted (null)
  and are cleared by an explicit empty value (`""` / `[]`). Deliberate exceptions, named in the tool description:
  `origin` applies to new pages only; `linksTo` / `sourceRtIds` only add links (removal is `knowledge_link remove=true`);
  `changeNote` and the run metadata belong to the new version only.
- **Run data** (AB#8657, write path W1 of AB#8486: the pipeline node `McpToolCall@1` writes after the LLM run):
  `knowledge_upsert_page` takes `inputTokens`, `outputTokens`, `cost` (USD), `reviewerRounds`, `latencySeconds` (AB#8719) and `runRef` besides
  `promptName` / `promptVersion` / `modelId`, carried by `KnowledgeRunMetadata` into `BuildWrite`, which writes only the
  non-null values to the version attributes `PromptTokens`, `CompletionTokens`, `CostUsd` (Double), `ReviewerIterations`,
  `RunRef`, `LatencySeconds` (Double). `KnowledgeTools.ValidateRunData` runs before the tenant is opened: negative numbers (all named in one message),
  NaN/infinite cost or latency, a blank or > 200 character `runRef` = `InvalidArgument`. `runRef` passes the content guard like
  `changeNote`. Cache read/creation tokens are not stored: System.Ai has no attribute for them yet (question on AB#8657).
- **Prototype JSON**: `KnowledgeSectionDto` / `KnowledgeCitationDto` / `KnowledgeHallucinationFlagDto` carry the JSON names of the
  KnowledgeCapture prompts (`heading`, `sectionContent`, `sortOrder`, `sectionRef`, `sourceQuote`, `sourceAnchor`, `sourceRtId`,
  `claim`, `reason`); cited `sourceRtId`s that are SourceArtifacts are linked with `GeneratedFrom`, others are reported in
  `UnresolvedSources`.
- **Search**: the engine text index (title, summary, section content) preselects up to 200 pages; the tool ranks them by
  term hits (title 3, summary 2, body 1, capped) and **drops pages with score 0** (AB#8566: the preselection returned
  pages without any query word, e.g. `knowledge-schema` for "Zebrafisch"); no match = 0 pages, `isSuccess: true` and the
  message "No knowledge page matches …", also with a kind/status filter. Without a `status` filter, pages whose current
  status is Rejected are left out (search and index); `status: Rejected` lists them, `knowledge_get` still serves them by
  slug (AB#9088: three rejected file-back Drafts on kb-test2 still ranked #1/#2/#5). The filter runs after the
  200-page text-index preselection, like the kind/status filters, so many Rejected pages can push matches out of it. The text index stems English words, so a term
  also scores on words with the same stem (`Services/Knowledge/KnowledgeStemmer.cs`: plural, -ing, -ed; "pipelines"
  scores on "pipeline"; a two-letter rest gets its "e" back only as vowel + consonant: using -> use, but being -> be,
  doing -> do, eyed -> ey) — otherwise the score-0 filter would drop real index matches. The repository API does not return
  MongoDB's `textScore` (the engine only sorts by it), which is why the tool scores itself. Words shorter than 3 characters are ignored; a
  query without a longer word is treated as no query (index of all pages, the message says it is not a match). Without
  a query it is the index of all pages.
- **Ranking and the no-answer hint** (AB#9165, `KnowledgeTools.Match`): field score (title 3, summary 2, sections 1
  capped at 5 per word) + slug 3 (one hit) + `TopicBonus` 15 per query word the title or slug names; `Terms` drops
  `StopWords` (function and filler words) unless the query has no other word; the stemmer reduces `apis`/`uris`/`kpis`
  (4-letter `-is`, not `this`). Each page carries `matchedTerms`, the response `unmatchedTerms`: query words no
  searchable page contains - any kind or status (Rejected only with `status: Rejected`), also beyond `limit`, so a
  filter never makes a word "missing". `HintTerms` drops `StopWords`, `MannerAdverbs` (a list, not the suffix -ly:
  "anomaly" must count) and typos (`TypoMinLength` 6+ letters, one edit from a page word; 5 would turn q12's "apple"
  into "apply"); `ProbeHintTermsAsync` then searches each remaining word on its own (`HintProbeCandidates` 20, at most
  `MaxHintProbes` 5 words, the rest give no hint), because the 200-page preselection can leave out the page with a rare
  word; a word it finds leaves `unmatchedTerms` too. A probe failure counts as found (no hint).
  From one confirmed word the message is `NoAnswerHint`: it keeps tool answers (AB#9148) and answered parts, and says
  a name of the user's own app or tenant is no reason by itself. Limit: a question whose words are all on some page
  gets no hint (q23 "Refinery Studio Wi-Fi network"); slugs are not in the text index. `KnowledgeSearchRankingTests` pins,
  against the eval tenant's pages (`Tools/Fixtures/knowledge-search-eval-1.1.2.json`: builder pages + eval corpus,
  section text as `word:count`) and both forms of every eval question (recorded queries of the live eval 1.1.2 and
  the question texts): top 3 for all 38 answerable forms, 0 hints on them, the hint on every no-answer question text.
  Re-cut the fixture when the pages change much (provenance in its description). Cause: M1 live eval 2026-11-16
  (finding 3 ranking, finding 14 q20).
- **Answering rules** (AB#9088, `KnowledgeConventions.AnsweringRules`): every successful `knowledge_search` response
  also carries `answeringRules`, a code constant on top of the tenant's conventions that no schema page replaces: cite
  only pages opened with `knowledge_get` for this answer (a slug from a search result, index page or earlier answer is
  not enough); the description of one of this server's own tools is also a source when the answer recommends or uses
  that tool, cited as `[tool:<name>]` (AB#9148, `ToolRule`; M1 end-to-end test on m1-e2e could not recommend
  `add_client_to_role`); and when neither an opened page nor a tool answers reply with exactly
  `No knowledge page answers this question.` and cite nothing. The server instructions and the prompt
  `octomesh-knowledge-first` carry the same three rules (`CitationRule`, `ToolRule`, `NoAnswerRule`), and the no-match
  message of the search names the sentence. Cause: M1 eval on kb-test2
  2026-11-16 (q7/q18 cited unopened pages, q20 listed gaps with citations instead of the sentence).
- **Answering conventions** (AB#8712, `Services/Knowledge/KnowledgeConventions.cs`): every successful `knowledge_search`
  response carries `answeringConventions`, the section "Answering questions" of the schema page `knowledge-schema`, and
  `answeringConventionsSource` (`knowledge-schema@<version>` or `default`). The page is read with the caller's session on
  every search (one lookup by well-known name), and only a **Verified** text counts (every MCP write sets Draft, so an AI
  client with write rights could otherwise inject instructions into every later response; review of PR #66). While the
  page is **Draft**, the newest Verified version the tenant approved or adopted applies (AB#8780; source
  `knowledge-schema@<that version>`, one more read of the versions): every tenant version (only a person verifies), and a
  Blueprint version only when it is in the ancestor chain of the current version - a seeded version the tenant never
  adopted (new or superseded) never replaces the approved text. A newer Rejected version of that set gives the default. A caller who cannot read it, a page that is
  InReview or Rejected, or a Draft page without a Verified version gets the built-in default
  (`KnowledgeConventions.DefaultText`), as does a tenant without the page or the section, or a failed read (the search
  never fails because of it). The section text is bounded to 2000 characters: cut at the last whitespace before the limit
  plus a fixed marker, deterministic, never inside a surrogate pair. `KnowledgeConventionsCache` (singleton) keeps the bounded text per tenant keyed by
  page rtId and `CurrentVersion`; a new page version is a miss. Clients therefore follow the conventions without opening
  the schema page first (draft eval runs of AB#8487 scored 5/6 and 4/6 when the client never read it).
- **Optimistic locking**: `expectedVersion` (the page's `CurrentVersion`) on upsert and restore; stale = `VersionConflict`, nothing
  written. Independent of it, every page write re-reads the page inside its transaction (`ApplyAsync`): a page that moved on
  since the tool read it, or a MongoDB write conflict with a writer that commits later, is `VersionConflict` as well, so two
  writers never both write version `n + 1` of an existing page. Not covered: two concurrent **creates** of the same new slug
  (no unique index on `rtWellKnownName`); the second one leaves a duplicate page.
- **Risk**: `knowledge_upsert_page`, `knowledge_restore_version` and `knowledge_set_status` are Medium (audit matters, a status
  change decides what answers may use); `knowledge_search`, `knowledge_get` and `knowledge_link` are Low.
- **Only write path**: the generic entity tools and `import_runtime_model` refuse the knowledge types (AB#8568, see
  "Knowledge types are written only by the knowledge tools"). There is no knowledge delete tool; pages are set to `Rejected`.
- Tests: `Tools/KnowledgeToolsTests` (in-memory repository that applies inserts, updates and associations like the engine).

### Knowledge first: instructions, prompt, fallback (AB#9087)

- The `initialize` result's `instructions` come from `KnowledgeFirst.ServerInstructions`
  (`Services/Knowledge/KnowledgeFirst.cs`), a `const` - never make it tenant-configurable (tenant text in every client
  session is an injection path; same reason as `KnowledgeConventions`). The five area index slugs live there too.
- Server options and capabilities are registered only through `Configuration/McpServerSetup.cs`
  (`AddMcpServer(McpServerSetup.ConfigureOptions)...WithOctoCapabilities()`: tools, resources, prompts of the
  assembly). `tests/McpServices.Tests/Prompts/KnowledgeFirstProtocolTests.cs` registers the same setup over in-memory
  pipes and asserts the real `initialize`, `prompts/list` and `prompts/get` responses - use that pattern for any other
  protocol-level assertion.
- Prompts are `[McpServerPromptType]` classes in `src/McpServices/Prompts/` (`octomesh-knowledge-first`, argument
  `question`).
- `knowledge_search` sets `builderKnowledgeHint` when the tenant has no Skill page (kind 4; blueprint
  `System.Ai.Builder` missing). It decides from the read pages when possible, otherwise it reads one probe page list
  (`KnowledgeTools.SkillProbeCandidates`, no text search); the test mock counts the probe separately (`_skillProbes`).
