# Review focus: octo-mcp-service

Check these points in addition to the general review. Each point names its source: a section of `CLAUDE.md` or the pull
request whose review found the problem.

## Tool contract

- Every new tool has unit tests for the happy path, the unauthenticated case, missing required arguments and, for a
  destructive tool, refusal without `confirm` (`CLAUDE.md`, "Mandatory Conventions" 1 and 5).
- Tools reach the SDK only through the `*ClientContext` helpers and per-request SDK clients, never through the factory
  directly (except the documented system-scoped Bot one-offs) or a shared client (`CLAUDE.md`, "Mandatory Conventions"
  2, 2a and 7, "Things NOT to do").
- Destructive tools keep `confirm` opt-in (never default `true`) and carry a `[McpRisk]` level that fits their blast radius
  (`CLAUDE.md`, "Mandatory Conventions" 5 and 8).
- Tools report to the caller through the response envelope (`Message` / `ErrorMessage`), never through `Console`, and do
  not accept base64 file content as a parameter; files go through the file-transfer endpoints (`CLAUDE.md`, "Things NOT
  to do", "File I/O Architecture").

## Secrets and outbound requests

- A new or changed secret parameter is marked `SENSITIVE:` and scrubbed from every `ErrorMessage` of its tool (as
  `UserManagementTools.Scrub` does), a new or changed error path that can carry a token passes `ErrorTextRedactor.Redact`,
  and `Program.cs` keeps `AddMcpPayloadLogFilter` (`CLAUDE.md`, "Mandatory Conventions" 2a, "Sensitive parameters"; PR #74).
- Outbound fetches follow the knowledge-source URL policy (no private or link-local targets, no redirects around the
  check) (`CLAUDE.md`, "Outbound fetches: the knowledge-source URL policy").
- A user value that goes into an SDK route path is accepted only as one safe segment: validated against an allow-list that
  also rejects the dot segments `.` and `..`, as `IsCatalogNameSegment` in `BlueprintTools.cs` does, or escaped with
  `Uri.EscapeDataString` and still refused when it is `.` or `..`, because escaping leaves dot segments intact and they
  address another route (PR #86).

## Knowledge tools

- Knowledge types are written only by the knowledge tools, writes are versioned with the documented exceptions, and the
  content guard runs on every write (`CLAUDE.md`, "Knowledge types are written only by the knowledge tools", "Content guard
  of knowledge writes", "Platform updates of Blueprint pages").
- Paged and multi-page reads keep results already collected when a later page fails, report a truncated scan as
  truncated, and do not infer "nothing there" from a capped batch (PR #73, PR #79, PR #82).

## Paging

- Entity query tools page as documented, and tests assert that the query succeeded before they check paging
  (`CLAUDE.md`, "Paging of the entity query tools"; PR #72).
