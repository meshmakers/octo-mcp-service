---
description: Agent management tools (create_agent, update_agent, set_agent_enabled, list_agents, get_agent), mandate validation, the service-account client and its secret, the write channel of the agent types and the static mandate check of every tool call (refusal codes).
applies_to: **/Tools/AgentTools.cs, **/Services/Agents/**, **/Models/AgentResponses.cs
---

# Agent tools

Five tools manage the agents of a tenant: the System.Ai types `Agent` and `Mandate` (System.Ai 4.12.0 or later; a
tenant without them gets `NoAgentTypes`) and the agent's service-account client in the tenant identity.

| Tool | Risk | What it does |
|---|---|---|
| `create_agent` | Medium | Writes the `Agent`, a new `Mandate` (or links an existing one with `mandateRtId`) and the `GovernedBy` association, then creates the service-account client with the roles of the call |
| `update_agent` | Medium | Changes agent fields, the agent's own mandate, or switches to another mandate; `roles` replaces the client's roles |
| `set_agent_enabled` | Medium | Kill switch: the agent's `Enabled` and the client's `IsEnabled` together |
| `list_agents` | Low | All agents with their mandates (no identity call) |
| `get_agent` | Low | One agent by runtime id or name, with mandate and the client's enabled state |

An agent is addressed by its runtime id or its name (case-insensitive; names are unique per tenant).

## Who may write

The three writing tools are for **tenant administrators**: the caller needs the role `TenantManagement` or
`UserManagement` in the tenant (the roles the identity requires for its client methods). Everyone else is refused with
`NotTenantAdministrator` before anything is written, also a client that holds only `octo_api`. In addition, a caller
whose client id is the service account of **any** agent of the tenant is refused with `AgentCaller` by all three
writing tools, whatever roles it holds (client-credentials and on-behalf-of tokens alike): agents never manage agents,
their own included, so an agent cannot raise its own or a shared mandate, switch a kill switch or create a wider agent.
When the agents cannot be read, the writing tools refuse (`AgentCaller`, fail closed). `list_agents` and `get_agent`
need no admin role.

## Agent administration goes through the agent API (AB#9729)

System.Ai 4.12.0 declares `writeAccess { channels: [System.Ai/AgentAdministration] }` on `Agent` and `Mandate`. No
principal kind is admitted by itself, so the engine refuses every write of these types (create, update, delete, the
`AgentToolGrant` records and the `GovernedBy`/`WakesOn` edges) with message 144 unless the session carries the channel:
GraphQL and REST of the asset repository, ImportRt and the generic entity tools of this server (`create_entity`,
`update_entity`, `delete_entity`, ...) are refused for every caller, a signed-in tenant administrator included. Only the
three writing agent tools put the channel on their session (`AgentModel.WriteChannel`), after the admin check and the
agent-caller refusal; the caller's identity and roles stay on the session. Blueprint seeds (System.Ai.Builder) and system
sessions are not restricted. A later Studio UI for agents builds on this API, not on generic entity writes.

## Two paths, caller's rights on both

- **Agent and mandate** are written directly in the engine with the caller's own session (family 2,
  `RuntimeSecurityContextResolver`) plus the write channel, so the caller's CK roles and data permissions still decide;
  a refused write comes back as `WriteRefused`.
- **The client** is created, switched and given roles through the System.Identity CK methods (`CreateClient`,
  `UpdateClient`, `SetRoles`, `DeleteClient`), which check the caller's identity roles (`METHOD_FORBIDDEN`).

`create_agent` writes the entities first, so a caller who may not write them changes nothing in the identity. When the
identity step fails, the tool deletes the client it created and the agent and mandate it wrote (`IdentityFailed`; the
message names anything that could not be removed). `set_agent_enabled false` switches the agent off first and the
client second, so the kill switch holds even when the identity call fails (the response says the client is still
enabled); `true` switches the client on first and turns it off again when the agent write fails.

## Static mandate check of every tool call (AB#9729)

`AgentMandateFilter` is a `tools/call` request filter in front of every tool of this server (registered in
`McpServerSetup.WithOctoCapabilities`); `AgentMandateGuard` decides. A caller whose `client_id` is the
`ServiceAccountClientId` of an agent in the token's own tenant (`tenant_id`) runs a tool only when the agent is enabled and
its mandate grants the tool at the tool's risk (`IToolRiskRegistry`). This covers client-credentials and on-behalf-of
tokens of the agent's client. Every other caller (people, octo-cli, Studio, other clients) is not affected. There is no
run token and no policy decision point yet (concept §3.4, AB#8461): no argument constraints, change budget or WriteMode
`Propose` are enforced here.

Checks, in order: exactly one agent with the client id → `Enabled` is true → exactly one readable `GovernedBy` mandate → a
grant matches the tool (exact name, or a prefix ending in one `*`; a bare `*`, a `*` elsewhere or an unknown `MaxRisk`
grants nothing) → tool risk ≤ the highest `MaxRisk` of the matching grants (a grant without `MaxRisk` admits Low) and ≤ the
mandate's `RiskCeiling`.

| Code | When |
|---|---|
| `AgentDisabled` | The agent's `Enabled` is not `true` (kill switch; an absent value counts as disabled) |
| `ToolNotGranted` | No grant of the mandate matches the tool |
| `RiskAboveCeiling` | The tool's risk is above the matching grant's `MaxRisk` or above the mandate's `RiskCeiling` (the message says which) |
| `MandateUnavailable` | Fail closed: no `tenant_id` on a token with a client id, the agents or the mandate cannot be read (store error), several agents with the same client id, the agent list at its read limit (`AgentModel.MaxAgents`), no or several mandates, a mandate that does not exist, a risk ceiling outside Low..High |

A refusal is an error result (`isError: true`) whose text is JSON: `isSuccess` false, `errorCode`, `errorMessage` (the
reason), `tool`, `agent`. The tool does not run. Each decision about an agent caller is logged (client id, tenant, tool,
agent, code; refusals at Warning; no token content or arguments).

The lookup runs as the system, because an agent's client usually cannot read the agent types, and only in the token's own
tenant: an agent of another tenant with the same client id is never consulted. There is **no cache**: every call reads the
agent and its mandate, so a disable or a grant change takes effect on the next call. The dynamic CK method tools have no
risk annotation and count as Low.

## Mandate

| Value | Default | Rule |
|---|---|---|
| `toolGrants` | none (grants nothing) | Each `toolPattern` is a tool name of this server or a prefix ending in `*` that matches at least one; a bare `*` is refused. An unknown name is refused with `UnknownTool` and a message naming it; nothing is written |
| `maxRisk` of a grant | Low | At most the mandate's `riskCeiling` (`RiskAboveCeiling`); always written explicitly |
| `riskCeiling` | Low | Low, Medium, High. Lowering it below a kept grant is refused |
| `changeBudgetPerRun` | 0 | 0 or more |
| `writeMode` | Propose | Propose or Direct |
| `allowedCkTypes` | absent | `Model/Type` ids; `[]` on update clears the list |
| `argumentConstraints` of a grant | absent | A JSON object |

The tool names come from the risk registry (`IToolRiskRegistry`), i.e. the static tools of this assembly; the dynamic
CK method tools cannot be granted yet.

The engine materialises no defaults inside records, so a stored grant without `MaxRisk` reads as Low
(`AgentModel.ReadMandate`). A mandate may govern several agents (a blueprint can share one); `update_agent` refuses to
change such a shared mandate (`SharedMandate`) and offers `mandateRtId` to switch the agent instead.

A change of instructions, model or mandate raises the agent's `Version`; name, autonomy, runner, sponsor and roles do
not.

## Service-account client and its secret

The client gets the id `agent-<name in lower-case words>` (or `serviceAccountClientId`), grant type
`client_credentials`, scope `octo_api`, a required secret and exactly the roles of `roles` (none by default). The secret
is never in a response, an error message, a log line or the `Agent` entity: `clientSecret` is passed to `CreateClient`
only (the secret-carrying identity methods need `Methods:Identity:EnablePasswordMethods`), and without it a random secret
is generated and discarded. The agent runtime does not use that secret: it obtains the agent's credential through its
own path (run-scoped agent tokens and the key reveal at run start in octo-ai-services). Until then a person who needs
to run as the agent sets a new secret with `update_client_secret`.

## Tests

`tests/McpServices.Tests/Tools/AgentToolsTests.cs`: an in-memory repository plus the CK method fake of the identity
client. Covered: tenant-admin gate (the agent's own role-less client, another role-less client, a user without an admin
role; reads still work), agent callers (own and other agent's client, on-behalf-of token, failed lookup), the write
channel on the writing sessions only, the generic entity tools without channel, narrow defaults, unknown tool (name in the message, nothing written), prefix grants, ceiling, a caller
without write rights (refused, identity untouched), identity failure (entities removed, secret not echoed), roles, shared
mandate, version raise, kill-switch order in both directions, list/get, risk classification.

`tests/McpServices.Tests/Services/AgentMandateGuardTests.cs`: every refusal code, grant patterns, `MaxRisk` absent and
unknown, the kill switch and grant changes on the next call (no cache), every fail-closed path, the tenant boundary and
on-behalf-of tokens. `AgentMandateFilterProtocolTests.cs`: the filter in the real server over in-memory MCP pipes (refused
calls never run the tool; callers that are no agent are unaffected).
