using System.Net;
using System.Text.Json;
using Meshmakers.Octo.Sdk.ServiceClient;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Error text of a refused identity call (AB#8094, server side AB#5861): identity answers a missing role with
///     <c>403 {"message": "...requires the role 'SecretManagement'..."}</c>. The SDK keeps the response body in the text of the
///     <see cref="ServiceClientResultException" /> (<c>Forbidden: {"message":"..."}</c>); the message is read here once.
/// </summary>
public static class IdentityForbiddenMessage
{
    /// <summary>Generic text for a 403 without a body or without a usable <c>message</c>.</summary>
    public const string GenericAccessDenied =
        "Access denied (HTTP 403): the identity service refused the call without naming a reason. Writing API secrets " +
        "needs the tenant role 'SecretManagement' (TenantManagement/UserManagement only while the cluster runs " +
        "SecretManagementRoleEnforcement=Warn); for another tenant the user must be provisioned there " +
        "(provision_current_user_as_admin).";

    /// <summary>
    ///     The text to return to the caller: the <c>message</c> of a 403 body verbatim, the generic access-denied text for a 403
    ///     without one, the exception text for every other error.
    /// </summary>
    public static string Describe(Exception exception)
    {
        if (exception is not ServiceClientResultException { HttpStatusCode: HttpStatusCode.Forbidden } forbidden)
        {
            return exception.Message;
        }

        return TryReadMessage(forbidden.Message) ?? GenericAccessDenied;
    }

    private static string? TryReadMessage(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text.AsMemory(start));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("message", out var element) &&
                element.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(element.GetString()))
            {
                return element.GetString();
            }
        }
        catch (JsonException)
        {
            // not a JSON body: generic text
        }

        return null;
    }
}
