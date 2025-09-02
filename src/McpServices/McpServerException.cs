using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.McpServices;

/// <summary>
///     Exception type for errors occurring in the MCP server.
/// </summary>
public class McpServerException : Exception
{
    /// <inheritdoc />
    public McpServerException()
    {
    }

    /// <inheritdoc />
    public McpServerException(string message) : base(message)
    {
    }

    /// <inheritdoc />
    public McpServerException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception CkTypeIdNotSet(OctoObjectId entityRtId, string tenantId)
    {
        return new McpServerException(
            $"The Construction Kit Type ID is not set for entity with RT ID '{entityRtId}' in tenant '{tenantId}'. " +
            "This is required for all entities in the MCP server.");
    }

    internal static Exception EntityNotFound(RtEntityId rtEntityId)
    {
        return new McpServerException(
            $"The entity with RT ID '{rtEntityId}' was not found.");
    }
}