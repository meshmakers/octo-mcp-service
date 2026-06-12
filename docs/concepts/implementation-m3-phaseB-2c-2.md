# implementation-m3-phaseB-2c-2 — apply_custom_app_scaffold MCP tool

Status: **Design sketch (drafting)** · Counterpart: `octo-ai-services/docs/concepts/implementation-m3-phaseB-2c.md` (B-2c overall)

## 1. What this commit ships

Gate-19b's PR #8 on `meshmakers/ai-sandbox-app` (AuditLog page) showed where the Application track spends Claude turns: the agent design-thinks every canonical file from first principles. The breakdown of `~/pages/audit-log/audit-log.{ts,html,scss}`, `~/services/audit-log.service.ts`, `~/services/audit-log.service.spec.ts`, `~/models/audit-log-entry.ts`, `~/graphQL/getAuditLog.graphql`, plus the two cross-cutting edits (`app.routes.ts` + `custom-svg-icons.ts`) is 9 file mutations, each preceded by 1-2 turns of "what shape does this take?".

The shape is canonical — same template-repo, same Apollo + signals + standalone pattern. The agent re-derives it every session because no tool hands it the canonical content.

`apply_custom_app_scaffold` takes a `plan_custom_app_scaffold` output PLUS the CK type schema(s) the operator's goal references, and returns a structured operation list the agent applies via its built-in `Write`/`Edit` tools:

```
{
  IsSuccess: true,
  WriteOps: [
    { Path: "src/custom-app/src/app/pages/audit-log/audit-log.ts",         Content: "<standalone component, signals + inject>" },
    { Path: "src/custom-app/src/app/pages/audit-log/audit-log.html",       Content: "<Kendo Grid skeleton, TODO column rows>" },
    { Path: "src/custom-app/src/app/pages/audit-log/audit-log.scss",       Content: "" },
    { Path: "src/custom-app/src/app/services/audit-log.service.ts",        Content: "<Apollo service, fetchXxx() + mapXxxResult()>" },
    { Path: "src/custom-app/src/app/services/audit-log.service.spec.ts",   Content: "<5 cases for mapXxxResult>" },
    { Path: "src/custom-app/src/app/models/audit-log-entry.ts",            Content: "<DTO interface from CK type attributes>" },
    { Path: "src/custom-app/src/app/graphQL/getAuditLog.graphql",          Content: "<query GetAuditLog stub with TODOs>" },
  ],
  EditOps: [
    { Path: "src/custom-app/src/app/app.routes.ts",                        OldString: "...", NewString: "..." },
    { Path: "src/custom-app/src/app/services/my-command-settings.service.ts", OldString: "...", NewString: "..." },
    { Path: "src/custom-app/src/app/custom-svg-icons.ts",                  OldString: "...", NewString: "..." },
  ],
  NextSteps: [
    "Fill in the GraphQL query body in getAuditLog.graphql (replace TODOs with actual field names from the CK type's attributes).",
    "Run `npm run codegen` to emit getAuditLog.generated.ts + refresh globalTypes.ts.",
    "Fill in audit-log.html's grid columns to match the CK type's attribute paths.",
    "Run `npm run lint && npm run test:ci && npm run build:prod` before pushing."
  ]
}
```

The MCP server cannot write to the agent's workspace directly (tools return JSON over stdio; the agent applies). So the tool centralizes design while keeping the agent in control of the write.

## 2. Why this design and not a bigger one

**Considered**: a tool that directly mutates the workspace. Rejected — the MCP server has no path to the agent's PVC (`/workspaces/sessions/<sid>/repo/`). Adding one would require a new file-transfer channel similar to `IFileTransferStore` but writing INTO the workspace, which is an architectural-rewrite-shaped change for a Phase-B friction reduction.

**Considered**: a tool that emits all files but does NOT pre-fill schema-specific bits (no GraphQL field list, no grid columns). Rejected as a meaningful reduction — the agent already knows the schema by the time it calls `apply`, and the skeleton's TODOs are themselves design work for the agent.

**Chosen**: a tool that takes the schema(s) AS INPUT (parameter `typeSchemas`), pre-fills attribute-derived bits where the mapping is mechanical (DTO interface fields, GraphQL leaf fields), and leaves design choices to the agent (which attributes show as columns, which order, which formatting). The agent's remaining work is: pick the columns + author the grid template body + (optionally) tweak the service. ~3 turns saved per page, scaling linearly with pages-per-session.

## 3. Tool signature

```csharp
[McpServerTool(Name = "apply_custom_app_scaffold")]
[McpRisk(McpRiskLevel.Medium)]
[Description(
    "Expand a plan_custom_app_scaffold output into the canonical Custom-App file contents + " +
    "route/drawer/icon edits the agent then applies via Write/Edit. Pass the plan AND the " +
    "CK type schema(s) the pages bind to; the tool pre-fills attribute-derived shapes " +
    "(DTO interfaces, GraphQL leaf fields) and leaves column-selection / template-body / " +
    "service-wiring to the agent. Pure-logic — no I/O, no auth.")]
public static Task<ApplyCustomAppScaffoldResponse> ApplyCustomAppScaffold(
    McpServer server,
    [Description("Output of plan_custom_app_scaffold — Pages + DrawerItems + Routes.")]
    CustomAppScaffoldPlanResponse plan,
    [Description("CK type schemas the pages bind to, keyed by RouteSlug (e.g. {'audit-log': <schema>}). The tool pre-fills DTO + GraphQL fields from attributes; pages without an entry get empty stubs.")]
    Dictionary<string, ApplyScaffoldTypeBinding> typeBindings,
    [Description("Workspace-relative path to app.routes.ts. Defaults to 'src/custom-app/src/app/app.routes.ts'.")]
    string? appRoutesPath = null,
    [Description("Workspace-relative path to my-command-settings.service.ts. Defaults to 'src/custom-app/src/app/services/my-command-settings.service.ts'.")]
    string? commandSettingsPath = null,
    [Description("Workspace-relative path to custom-svg-icons.ts. Defaults to 'src/custom-app/src/app/custom-svg-icons.ts'.")]
    string? customSvgIconsPath = null)
```

`ApplyScaffoldTypeBinding` carries: the CK `TypeId` (so the GraphQL query name knows what to call — `GetAiAuditEvent`), the operation name (`systemAiAuditEvent` — derived from PascalCase → camelCase), and the list of `(name, primitiveType, isOptional)` triples for each attribute so the DTO + GraphQL skeleton can name them. The agent passes this from its own `get_type_schema` + naming-convention output.

## 4. Response shape

```csharp
public sealed class ApplyCustomAppScaffoldResponse
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Message { get; set; }
    public List<WriteOp> WriteOps { get; set; } = new();
    public List<EditOp> EditOps { get; set; } = new();
    public List<string> NextSteps { get; set; } = new();
}

public sealed class WriteOp
{
    public required string Path { get; init; }
    public required string Content { get; init; }
    public required string Purpose { get; init; }   // "page-component", "service", "graphql-query", "dto", "spec"
}

public sealed class EditOp
{
    public required string Path { get; init; }
    public required string OldString { get; init; }
    public required string NewString { get; init; }
    public required string Purpose { get; init; }   // "route-registration", "drawer-item", "icon-import"
}
```

The `Purpose` field lets the agent log the apply-loop semantically (helpful for the trace + for the NextSteps message). The agent's `Write`/`Edit` tool calls consume `(Path, Content)` and `(Path, OldString, NewString)` directly.

## 5. Content templates — anchored to template-repo conventions

The static template strings live in `Resources/CustomAppTemplates/` as embedded resources, NOT as string literals in C#. Each template gets a fixed name + version + golden-file unit test that diffs it against the equivalent file in `meshmakers/template-repo`'s `pages/developer-info/`, `services/diagnostics.service.ts`, `graphQL/getMaintenanceMode.graphql` reference set. CI fails if the template drifts from the canonical shape — that's the only mechanism that prevents content drift over time.

Templates use a tiny Handlebars-like syntax: `{{ClassName}}`, `{{RouteSlug}}`, `{{TypeId}}`, `{{Attributes}}` (a loop). The expansion is plain string-replace + `foreach` for the loops — no real Handlebars engine.

Templates ship with this commit:
- `Page.ts.tpl` — standalone component, signals, Kendo Grid + DatePipe imports
- `Page.html.tpl` — Kendo Grid skeleton with a refresh button + a TODO comment for column rows
- `Page.scss.tpl` — empty (intentional)
- `Service.ts.tpl` — Apollo service, `fetchXxx()`, extracted `mapXxxResult()` pure function
- `Service.spec.ts.tpl` — 5 vitest cases for `mapXxxResult` (null, empty, one row, two rows, malformed edge)
- `Model.ts.tpl` — DTO interface, fields from CK attributes
- `Query.graphql.tpl` — `query GetXxx($first: Int)` with `runtime.<operation>` and TODO leaves

The route/drawer/icon edits are not template-based — they're constructed by reading the file (the agent already has the path) and computing a precise `(OldString, NewString)` pair based on the well-known insertion points (last `]` of the `children:` array; last `id:` entry of the `commandSettings` array; last import line of `custom-svg-icons.ts`). The tool emits the `OldString` + `NewString` so the agent's `Edit` call applies them deterministically.

## 6. Commit ladder

| # | Commit | Touches |
|---|---|---|
| 0 | `docs(mcp): implementation-m3-phaseB-2c-2.md design sketch — apply_custom_app_scaffold` | this file |
| 1 | `feat(mcp/custom-app): apply_custom_app_scaffold tool + templates + tests (B-2c-2)` | tool + 7 template files + response DTOs + xUnit tests + CLAUDE.md update |
| 2 | (back in octo-ai-services) `feat(ai/briefing): list apply_custom_app_scaffold in Application tool palette (B-2c-2)` | `Application/CLAUDE.md` |

## 7. Tests (commit 1)

| Test | What it pins |
|---|---|
| `Apply_HappyPath_PreFillsAttributesFromBinding` | A 1-page plan + 1 type binding → DTO has the binding's attributes; GraphQL query stubs the leaves. |
| `Apply_NoTypeBinding_EmitsEmptyStubs` | A page with no binding → DTO is `interface XxxEntry {}`, GraphQL has TODO body. |
| `Apply_MultiplePages_EmitsOpsPerPage` | A 3-page plan → 21 WriteOps + 3+ EditOps (one route + one drawer item each + one icon list edit). |
| `Apply_EmptyPlan_ReturnsValidationError` | Plan with no Pages → `IsSuccess=false`. |
| `Apply_PlanWithoutSuccess_Rejects` | Plan whose `IsSuccess=false` → tool refuses with explicit message ("pass a successful plan output"). |
| `Apply_TemplateGoldenFiles_MatchCanonical` | Each template, when expanded with a fixed binding, matches a `.expected.txt` golden file in tests. CI catches template drift. |
| `Apply_RouteEdit_InsertsBeforeChildrenClose` | The constructed route EditOp's OldString contains `]` (children close) and NewString preserves it after insertion. |
| `Apply_DrawerEdit_InsertsAtEndOfCommandSettings` | The drawer EditOp inserts at the end of the array, not in the middle. |

Mirror the existing `CustomAppGenerationToolsTests` pattern: xUnit + FluentAssertions + `TestBase`. No SDK mocks needed (pure-logic).

## 8. Open questions

1. **What if `app.routes.ts` has a non-standard shape** (e.g. eager imports, no `:lang` children)? **Decision**: the tool defines a precise `OldString` pattern; if no match, EditOp omits the route + NextSteps gets a `"Manual route registration needed — app.routes.ts does not match expected shape"`. The agent then hand-edits. No crash, graceful fall-back.
2. **What if the operator names the page something the CK type doesn't have?** (e.g. asks for an "AuditLog" page but the binding is `SystemDevice`.) **Decision**: the tool blindly fills the DTO from the binding's attributes regardless. The mismatch surfaces at compile time, and the agent sees the build:prod error and rolls back. Cleaner than the tool guessing at semantics.
3. **PR #8's actual file content uses `DatePipe` + `GridModule`** — should the template import those unconditionally? **Decision**: yes for `Page.ts.tpl`; AuditLog-style list pages are the default Application track output. If the operator wants a different shape (form, dashboard), they pick a different template via a future `templateId` parameter — not in this slice.
4. **Multi-tenant deployments** — the embedded resource files are baked into the MCP image. Operators on different tenants get the same template. **Decision**: fine. The templates are project-side conventions, not tenant-side data. A future "tenant-customised template" surface would extend the existing template-repo `init.sh` workflow, not the MCP tool.

## 9. Acceptance — Gate-20

(Coordinated with the B-2c parent doc's Gate-20.) Add to the verification:
- A session whose tool trace shows `apply_custom_app_scaffold` called once with a plan + 1+ type bindings.
- The agent's resulting Write/Edit calls match (modulo agent post-edits) the operation list from the tool.
- The PR's *.generated.ts is `npm run codegen` output (B-2c-1 takes credit), the *.graphql is the tool's stub + agent's fills, the page components are the tool's templates + agent's fills.
- Turn-count delta vs PR #8: target ≥4 turns saved on a single-page Application session.

## 10. Closing — to be filled at Gate-20

(Same as the parent doc — log session id, turn-count comparison, drift catches, any operator-surfaced friction.)
