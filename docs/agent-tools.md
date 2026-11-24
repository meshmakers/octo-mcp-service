---
description: Agent management tools (create_agent, update_agent, set_agent_enabled, list_agents, get_agent), mandate validation, the service-account client and its secret.
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
`NotTenantAdministrator` before anything is written, also a client that holds only `octo_api`. The check is needed
because the engine does not protect the agent types yet: without it a role-less caller could raise a mandate. In
addition, a caller whose client id is the agent's own service account is refused with `SelfModification` by
`update_agent` and `set_agent_enabled`, even when that client holds an admin role: an agent never changes itself.
`list_agents` and `get_agent` need no admin role.

## Two paths, caller's rights on both

- **Agent and mandate** are written directly in the engine with the caller's own session (family 2,
  `RuntimeSecurityContextResolver`), so the caller's CK roles decide; a refused write comes back as `WriteRefused`.
- **The client** is created, switched and given roles through the System.Identity CK methods (`CreateClient`,
  `UpdateClient`, `SetRoles`, `DeleteClient`), which check the caller's identity roles (`METHOD_FORBIDDEN`).

`create_agent` writes the entities first, so a caller who may not write them changes nothing in the identity. When the
identity step fails, the tool deletes the client it created and the agent and mandate it wrote (`IdentityFailed`; the
message names anything that could not be removed). `set_agent_enabled false` switches the agent off first and the
client second, so the kill switch holds even when the identity call fails (the response says the client is still
enabled); `true` switches the client on first and turns it off again when the agent write fails.

The engine itself does not protect `Agent` and `Mandate` yet: a caller with write rights on the System.Ai types can still
change them through the generic entity tools or GraphQL. That protection (a write channel on the types, like the
knowledge pages have) belongs to the engine and System.Ai, not to these tools. The static mandate check (the tool-call filter that refuses calls outside the
mandate) is not part of these tools.

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
role; reads still work), self-modification, narrow defaults, unknown tool (name in the message, nothing written), prefix grants, ceiling, a caller
without write rights (refused, identity untouched), identity failure (entities removed, secret not echoed), roles, shared
mandate, version raise, kill-switch order in both directions, list/get, risk classification.
