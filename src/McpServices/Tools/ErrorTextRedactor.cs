using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     AB#8418: removes credentials from an error text before a user tool returns it to the MCP client.
///     <para>
///         A failed REST call of the SDK surfaces as an exception whose message carries the response body. A service that
///         runs with the ASP.NET Core developer exception page (the local stack) answers a 500 with a plain-text page that
///         echoes the request headers, including <c>Authorization: Bearer …</c> - found in the AB#8418 E2E run, where the
///         user lookup of <c>reset_user_password</c> hit such a 500 and the tool returned the caller's token. The patterns
///         are those of the octo-cli <c>ErrorOutputRedactor</c> (AB#8570): the header section of the developer exception
///         page, credential headers, bearer tokens and JWTs. Status, the server's message and the stack trace stay visible.
///     </para>
/// </summary>
internal static partial class ErrorTextRedactor
{
    /// <summary>Placeholder written instead of a removed value.</summary>
    public const string Placeholder = "[REDACTED]";

    /// <summary>Placeholder written instead of the request header section of a developer exception page.</summary>
    public const string HeaderSectionPlaceholder = "[request headers removed]";

    /// <summary>Returns <paramref name="text" /> without credentials; text without credentials is returned unchanged.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = DeveloperPageHeaderSection().Replace(text, m => m.Groups["head"].Value + HeaderSectionPlaceholder);
        result = CredentialHeader().Replace(result, m => m.Groups["name"].Value + m.Groups["sep"].Value + Placeholder);
        result = BearerToken().Replace(result, m => m.Groups["scheme"].Value + " " + Placeholder);
        result = Jwt().Replace(result, Placeholder);
        return result;
    }

    // The plain-text developer exception page ends with "HEADERS\n=======\n<name>: <value>\n…". The whole section is
    // dropped: request headers carry no diagnostic value for the caller and any of them may hold a credential.
    [GeneratedRegex(@"(?<head>^[ \t]*HEADERS[ \t]*\r?\n[ \t]*=+[ \t]*\r?\n)(?:[^\r\n]+(?:\r?\n|$))*",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex DeveloperPageHeaderSection();

    // "Authorization: Bearer x", "Cookie: a=b", "X-Api-Key=…", also quoted JSON-style ("Authorization":"…").
    [GeneratedRegex(
        """(?<name>["']?\b(?:proxy-authorization|authorization|set-cookie|cookie|x-api-key|api-key|apikey|x-auth-token|x-access-token|x-csrf-token|x-xsrf-token|x-amz-security-token)\b["']?)(?<sep>[ \t]*[:=][ \t]*)(?<val>"[^"\r\n]*"|'[^'\r\n]*'|[^\r\n]*)""",
        RegexOptions.IgnoreCase)]
    private static partial Regex CredentialHeader();

    // A bearer token anywhere in free text (RFC 6750 b64token, 8 characters or more, or shorter with a digit or -._~+/=).
    [GeneratedRegex(
        @"(?<scheme>\bbearer)[ \t]+(?!\[REDACTED\])(?:[A-Za-z0-9\-._~+/]{8,}|(?=[A-Za-z]*[0-9\-._~+/=])[A-Za-z0-9\-._~+/]+)=*",
        RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    // A JSON web token (header.payload.signature, header and payload base64url of a JSON object).
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();
}
