# Review focus: octo-mcp-service

Check these points in addition to the general review. Each point names its source: a section of `CLAUDE.md` or the pull
request whose review found the problem.

## Tool contract

- Every new tool has unit tests for the happy path, the unauthenticated case, missing required arguments and, for a
  destructive tool, refusal without `confirm` (`CLAUDE.md`, "Mandatory Conventions" 1 and 5).
- Tools reach the SDK only through the `*ClientContext` helpers and per-request SDK clients, never through the factory
  directly or a shared client (`CLAUDE.md`, "Mandatory Conventions" 2 and 7, "Things NOT to do").
- Destructive tools keep `confirm` opt-in (never default `true`) and carry a `[McpRisk]` level that fits their blast radius
  (`CLAUDE.md`, "Mandatory Conventions" 5 and 8).
- Tools report to the caller through the response envelope (`Message` / `ErrorMessage`), never through `Console`, and do
  not accept base64 file content as a parameter; files go through the file-transfer endpoints (`CLAUDE.md`, "Things NOT
  to do", "File I/O Architecture").

## Secrets and outbound requests

- No password, token or secret reaches a log, a JSON-RPC payload log or a tool response; error text that can carry one
  passes `ErrorTextRedactor.Redact`, and the payload log filter covers new sensitive parameters (`CLAUDE.md`, "Mandatory
  Conventions" 2a; PR #74, PR #85).
- Outbound fetches follow the knowledge-source URL policy (no private or link-local targets, no redirects around the
  check) (`CLAUDE.md`, "Outbound fetches: the knowledge-source URL policy"; PR #85).
- User values in SDK routes are encoded as one path segment (PR #86, `BlueprintTools.cs`).

## Knowledge tools

- Knowledge types are written only by the knowledge tools, writes are versioned with the documented exceptions, and the
  content guard runs on every write (`CLAUDE.md`, "Knowledge types are written only by the knowledge tools", "Content guard
  of knowledge writes"; PR #85).
- Paged and multi-page reads keep results already collected when a later page fails, report a truncated scan as
  truncated, and do not infer "nothing there" from a capped batch (PR #73, PR #79, PR #82).

## Paging and aggregation

- Entity query tools page as documented, and tests assert that the query succeeded before they check paging
  (`CLAUDE.md`, "Paging of the entity query tools"; PR #72).
