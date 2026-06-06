namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Risk classification for an MCP tool. Read by the AI Adapter worker's PreToolUse hook to decide
///     whether the tool call needs an approval gate before it runs. Independent of authentication and
///     authorisation; the worker uses this to drive its own user-facing safety story, not the MCP
///     server's access control.
/// </summary>
/// <remarks>
///     Convention: tools are <see cref="Low" /> by default — declare a higher level explicitly on
///     destructive, broad-scope or hard-to-reverse operations.
/// </remarks>
public enum McpRiskLevel
{
    /// <summary>
    ///     Read-only operations or single-instance writes with narrow blast radius (e.g.
    ///     <c>query_entities</c>, <c>create_entity</c>, <c>update_entity</c> on one instance).
    ///     The worker runs these without prompting the user.
    /// </summary>
    Low = 0,

    /// <summary>
    ///     Writes with broader effect that the worker should audit but not block — single-instance
    ///     deletes, schema-introspection-driven actions, bulk reads. The worker logs the call and
    ///     surfaces a status line in the UI but does not pause for approval.
    /// </summary>
    Medium = 1,

    /// <summary>
    ///     Destructive, schema-changing or production-affecting operations — bulk delete, dropping a
    ///     CK type or attribute, production deploy, force-push to main. The worker pauses on
    ///     PreToolUse and routes the proposed call through the SignalR-backed approval flow before
    ///     executing.
    /// </summary>
    High = 2
}
