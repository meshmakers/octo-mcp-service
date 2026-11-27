namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Checks a user value that a tool hands to an SDK method which places it into a request path. The SDK builds some
///     routes by plain interpolation (no escaping) and escapes others only partly (<c>AddUrlSegment</c> escapes '/' but
///     lets '.' and '..' through), so a value with '/', '?', '#', '%' or a dot segment could address another route.
///     Names that go into a path (blueprint, catalog and CK model ids) are type-like names: ASCII letters, digits, '.',
///     '-' and '_', and never "." or "..".
/// </summary>
internal static class RouteSegment
{
    /// <summary>True when <paramref name="value" /> is safe as exactly one path segment.</summary>
    public static bool IsSafe(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value is not "." and not ".." &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>The refusal text for a parameter that is not one safe path segment.</summary>
    public static string Refusal(string parameter) =>
        $"{parameter} may contain only ASCII letters, digits, '.', '-' and '_' and must not be '.' or '..'.";
}
