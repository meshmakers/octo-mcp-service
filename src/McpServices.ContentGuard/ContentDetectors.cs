using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Backend.McpServices.ContentGuard;

/// <summary>
///     The pattern detectors of the content guard. Pure functions, no state; every rule is tuned so the
///     knowledge the platform ships (skills, runbooks, memory notes with ids, hashes, paths and URLs) passes, while
///     anything that is a credential is refused.
/// </summary>
public static partial class ContentDetectors
{
    /// <summary>Minimum length of a token the high-entropy detector looks at.</summary>
    public const int HighEntropyMinLength = 32;

    /// <summary>Minimum Shannon entropy (bits per character) of a high-entropy token.</summary>
    public const double HighEntropyMinBitsPerChar = 4.2;

    /// <summary>Minimum length of a tenant secret value the guard compares; shorter values are too common to compare.</summary>
    public const int MinSecretLength = 8;

    // sk-ant- plus a key type with an optional version and a dash (api03-, admin01-, oat01-, sid01-). A bare mention of
    // "sk-ant-" (as in documentation that lists the prefix) does not match; anything that looks like the start of a key does.
    [GeneratedRegex(@"sk-ant-[a-z]+[0-9]*-", RegexOptions.CultureInvariant)]
    private static partial Regex AnthropicKeyRegex();

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]{3,40}-----\s*[A-Za-z0-9+/=\s:,\-]{16,}?-----END [A-Z0-9 ]{3,40}-----",
        RegexOptions.CultureInvariant)]
    private static partial Regex PemRegex();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    // GitHub tokens (AB#8702): classic prefixes ghp_ (personal), gho_ (OAuth), ghu_ (user-to-server), ghs_
    // (server-to-server), ghr_ (refresh) with at least 16 characters, and fine-grained github_pat_ tokens. The bare prefix
    // in documentation ("tokens start with ghp_") does not match.
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{22,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenRegex();

    // AWS access key ids (AB#8702): AKIA (long-term) or ASIA (temporary) plus 16 upper-case letters or digits.
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex AwsAccessKeyRegex();

    // Credential assignments (AB#8702): a key that ends in a credential word, an optional closing quote of a quoted key,
    // '=' or ':' (not '==', '=>', ':=' or '::') and the value: a quoted string or an unquoted run up to whitespace, quote,
    // backtick, comma or semicolon. Whether the value is a literal credential is decided in code (IsAssignedCredential).
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_.\-])(?<key>[A-Za-z0-9_.\-]*(?:password|passwd|secret|token|api[_\-]?key|access[_\-]?key|private[_\-]?key))[""']?(?<before>[ 	]*)(?<sep>[:=])(?![=>:])(?<after>[ 	]*)(?<value>""[^""
]*""|'[^'
]*'|[^\s""'`,;]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CredentialAssignmentRegex();

    // A value that is code, not a literal: a dotted member chain (options.Token, process.env.API_KEY) or a call/index.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex MemberChainRegex();

    // A lower-case kebab-case name (my-secret-name): the name of a Kubernetes secret or a Vault entry, not its value.
    [GeneratedRegex(@"^[a-z][a-z0-9]*(?:-[a-z0-9]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex KebabNameRegex();

    /// <summary>Minimum length of an assigned value the credential assignment detector refuses.</summary>
    public const int MinAssignedValueLength = 4;

    // Values that say "no value here": types, booleans, nulls and the words documentation puts in place of a secret.
    private static readonly HashSet<string> PlaceholderValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "null", "none", "nil", "true", "false", "undefined", "empty", "redacted", "masked", "hidden", "removed",
        "omitted", "secret", "password", "token", "required", "optional", "string", "number", "boolean", "bool", "any",
        "value", "placeholder", "example", "dummy", "fake", "unset", "default", "env", "vault"
    };

    // Candidate tokens: runs of the characters a base64 / base64url / hex key consists of.
    // '=' only as base64 padding at the end, so key=value pairs are judged by their parts.
    [GeneratedRegex(@"[A-Za-z0-9+/_\-]{32,}={0,2}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenCandidateRegex();

    // Scheme, host and path of a URL (not its query): share links and wiki links carry random-looking ids there.
    [GeneratedRegex(@"[a-z][a-z0-9+.\-]*://[^\s?#)>\]""'`]*", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPathRegex();

    // Splits text into words for the secret comparison: whitespace, quotes, brackets and common separators.
    private static readonly char[] WordSeparators =
        [' ', '\t', '\r', '\n', '"', '\'', '`', '(', ')', '[', ']', '{', '}', '<', '>', ',', ';', '|'];

    /// <summary>True when the text contains the start of an Anthropic API key.</summary>
    public static bool ContainsAnthropicKey(string text) => AnthropicKeyRegex().IsMatch(text);

    /// <summary>True when the text contains a PEM block with a body.</summary>
    public static bool ContainsPemBlock(string text) => text.Contains("-----BEGIN ", StringComparison.Ordinal) &&
                                                        PemRegex().IsMatch(text);

    /// <summary>True when the text contains a GitHub token (<c>ghp_</c>, <c>gho_</c>, <c>ghu_</c>, <c>ghs_</c>, <c>ghr_</c>, <c>github_pat_</c>).</summary>
    public static bool ContainsGitHubToken(string text) => GitHubTokenRegex().IsMatch(text);

    /// <summary>True when the text contains an AWS access key id (<c>AKIA...</c> or <c>ASIA...</c>).</summary>
    public static bool ContainsAwsAccessKey(string text) => AwsAccessKeyRegex().IsMatch(text);

    /// <summary>
    ///     True when the text assigns a literal value to a credential key: <c>password=...</c>, <c>DB_PASSWORD="..."</c>,
    ///     <c>"client_secret": "..."</c>, <c>api_key: ...</c>, <c>access_token = ...</c>. Prose that names a credential
    ///     without a value passes, and so do placeholders and references (<c>$VAR</c>, <c>${{ secrets.X }}</c>,
    ///     <c>&lt;your-password&gt;</c>, <c>***</c>, <c>...</c>), see <see cref="IsAssignedCredential" />.
    /// </summary>
    public static bool ContainsCredentialAssignment(string text)
    {
        foreach (Match match in CredentialAssignmentRegex().Matches(text))
        {
            var spaced = match.Groups["before"].Length > 0 || match.Groups["after"].Length > 0 ||
                         match.Groups["sep"].Value == ":";
            if (IsAssignedCredential(match.Groups["value"].Value, spaced))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The rule of <see cref="ContainsCredentialAssignment" /> for one assigned value. A quoted value is a credential
    ///     when it has no whitespace and is not a placeholder. An unquoted value directly after <c>=</c>
    ///     (<c>password=hunter</c>, the form of connection strings, env files and command lines) is a credential unless it
    ///     is a placeholder. An unquoted value after <c>:</c> or a spaced <c>=</c> (YAML, code, prose) is one only when it
    ///     also holds a digit or a symbol, so prose (<c>Password: see the vault</c>), types (<c>token: string</c>),
    ///     member chains (<c>token = options.Token</c>), calls and lower-case kebab names of secrets
    ///     (<c>existingSecret: octo-mongodb</c>) pass.
    /// </summary>
    /// <param name="value">The value as matched, quotes included.</param>
    /// <param name="spaced">True for <c>:</c> or an <c>=</c> with whitespace around it.</param>
    public static bool IsAssignedCredential(string value, bool spaced)
    {
        var quoted = value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0];
        var literal = (quoted ? value[1..^1] : value).Trim();
        if (literal.Length < MinAssignedValueLength || IsPlaceholder(literal))
        {
            return false;
        }

        if (quoted)
        {
            return !literal.Any(char.IsWhiteSpace);
        }

        if (!spaced)
        {
            return true;
        }

        if (literal.Contains('(') || literal.Contains('[') || MemberChainRegex().IsMatch(literal) ||
            KebabNameRegex().IsMatch(literal))
        {
            return false;
        }

        return literal.Any(c => !char.IsAsciiLetter(c));
    }

    private static bool IsPlaceholder(string literal)
    {
        // Only whole reference syntaxes count, never a single leading character: a real password may well start with
        // '#', '@', '&', '.' or '?' (review of PR #66).
        if (ReferenceRegex().IsMatch(literal) || literal.All(c => c == literal[0]) ||
            literal.All(c => c is '*' or '.' or '…' or '?'))
        {
            return true;
        }

        // Trailing punctuation and the non-null / nullable marks of type names (GraphQL String!, C# string?).
        var word = literal.TrimEnd('.', ',', ')', ']', '}', '!', '?');
        return PlaceholderValues.Contains(word) || word.StartsWith("your", StringComparison.OrdinalIgnoreCase) ||
               word.StartsWith("example", StringComparison.OrdinalIgnoreCase) ||
               word.Contains("...", StringComparison.Ordinal) || word.Contains('…');
    }

    // A reference instead of a value: $NAME / $env:NAME, ${...} and $(...), the template openers {{ ${{ #{ (the value
    // ends at the first whitespace, so "{{ .Values.x }}" arrives as "{{"), {name}, %NAME%, <placeholder ...> and
    // [REDACTED]-style brackets.
    [GeneratedRegex(
        @"^(?:\$(?:env:)?[A-Za-z_][A-Za-z0-9_]*|\$\{.*|\$\(.*|\{\{.*|#\{.*|\{[A-Za-z_][A-Za-z0-9_.\-]*\}|%[A-Za-z_][A-Za-z0-9_]*%|<[A-Za-z_][^<>]*>?|\[[^\[\]]+\])$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceRegex();

    /// <summary>True when the text contains a JSON Web Token.</summary>
    public static bool ContainsJwt(string text) => JwtRegex().IsMatch(text);

    /// <summary>
    ///     True when the text contains a long random-looking token: at least <see cref="HighEntropyMinLength" />
    ///     characters of the base64/base64url alphabet, upper case, lower case and digits mixed, and a character entropy
    ///     of at least <see cref="HighEntropyMinBitsPerChar" />, and characters switching class on at least
    ///     <see cref="MinClassSwitchRatio" /> of the positions. Pure hex strings (hashes, object ids, GUIDs), identifiers
    ///     made of words, file paths and the path part of URLs never match; URL query strings are checked.
    /// </summary>
    public static bool ContainsHighEntropyToken(string text)
    {
        // URL paths are not scanned (ids in share links); query strings are, a credential often travels there.
        var scanned = UrlPathRegex().Replace(text, " ");
        foreach (Match match in TokenCandidateRegex().Matches(scanned))
        {
            // Microsoft Teams channel and chat ids (19:<random>@thread.tacv2) are identifiers, not credentials.
            if (scanned.AsSpan(match.Index + match.Length).StartsWith("@thread.", StringComparison.Ordinal))
            {
                continue;
            }

            if (IsHighEntropyToken(match.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The rule of <see cref="ContainsHighEntropyToken" /> for one token.</summary>
    public static bool IsHighEntropyToken(string token)
    {
        var core = token.Trim('=', '-', '_', '/', '+');
        if (core.Length < HighEntropyMinLength)
        {
            return false;
        }

        bool upper = false, lower = false, digit = false, nonHex = false;
        var separators = 0;
        foreach (var c in core)
        {
            if (char.IsAsciiLetterUpper(c))
            {
                upper = true;
            }
            else if (char.IsAsciiLetterLower(c))
            {
                lower = true;
            }
            else if (char.IsAsciiDigit(c))
            {
                digit = true;
            }
            else
            {
                separators++;
            }

            if (!char.IsAsciiHexDigit(c) && c != '-')
            {
                nonHex = true;
            }
        }

        if (!nonHex || !(upper && lower && digit))
        {
            return false;
        }

        // Paths and URL paths (docs/concepts/x.md, wiki/1234/Release-Trains): a key that contains '/' still has a
        // segment of at least 16 characters that mixes upper case, lower case and digits; a path has none.
        if (core.Contains('/') && !core.Split('/').Any(IsRandomLookingSegment))
        {
            return false;
        }

        // Words joined by separators (file paths, slugs, identifiers): a key has few separators.
        if (separators * 8 > core.Length)
        {
            return false;
        }

        // Identifiers (camelCase, PascalCase, SCREAMING_CASE) keep a character class for several characters in a
        // row; a random key switches between upper case, lower case and digits on most characters.
        return ShannonEntropy(core) >= HighEntropyMinBitsPerChar && ClassSwitchRatio(core) >= MinClassSwitchRatio &&
               WordCoverage(core) < MaxWordCoverage;
    }

    /// <summary>
    ///     Maximum share of a random-looking token covered by runs of four or more lower-case letters. Random base64
    ///     stays below it in about 97 % of the cases; identifiers and paths built from words lie far above it.
    /// </summary>
    public const double MaxWordCoverage = 0.3;

    /// <summary>Share of the text covered by runs of at least four lower-case ASCII letters.</summary>
    public static double WordCoverage(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        int covered = 0, run = 0;
        foreach (var c in text)
        {
            if (char.IsAsciiLetterLower(c))
            {
                run++;
                continue;
            }

            covered += run >= 4 ? run : 0;
            run = 0;
        }

        covered += run >= 4 ? run : 0;
        return (double)covered / text.Length;
    }

    /// <summary>Minimum share of neighbouring characters of different classes for a random-looking token.</summary>
    public const double MinClassSwitchRatio = 0.3;

    /// <summary>
    ///     Share of neighbouring character pairs whose classes (upper case, lower case, digit, other) differ:
    ///     about 0.6 for random base64, below 0.2 for identifiers made of words.
    /// </summary>
    public static double ClassSwitchRatio(string text)
    {
        if (text.Length < 2)
        {
            return 0;
        }

        var switches = 0;
        for (var i = 1; i < text.Length; i++)
        {
            if (CharClass(text[i]) != CharClass(text[i - 1]))
            {
                switches++;
            }
        }

        return (double)switches / (text.Length - 1);

        static int CharClass(char c) => char.IsAsciiLetterUpper(c) ? 0 : char.IsAsciiLetterLower(c) ? 1 :
            char.IsAsciiDigit(c) ? 2 : 3;
    }

    private static bool IsRandomLookingSegment(string segment) =>
        segment.Length >= 16 && segment.Any(char.IsAsciiLetterUpper) && segment.Any(char.IsAsciiLetterLower) &&
        segment.Any(char.IsAsciiDigit) && ShannonEntropy(segment) >= 3.5 &&
        ClassSwitchRatio(segment) >= MinClassSwitchRatio && WordCoverage(segment) < MaxWordCoverage;

    /// <summary>Shannon entropy of the text in bits per character.</summary>
    public static double ShannonEntropy(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var counts = new Dictionary<char, int>();
        foreach (var c in text)
        {
            counts[c] = counts.TryGetValue(c, out var n) ? n + 1 : 1;
        }

        double entropy = 0;
        foreach (var n in counts.Values)
        {
            var p = (double)n / text.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    /// <summary>
    ///     True when the text, one of its words, or one of its words without surrounding punctuation has a fingerprint in
    ///     <paramref name="secretFingerprints" />.
    /// </summary>
    public static bool ContainsSecretValue(string text, IReadOnlySet<string> secretFingerprints)
    {
        if (secretFingerprints.Count == 0)
        {
            return false;
        }

        if (Matches(text.Trim()))
        {
            return true;
        }

        foreach (var word in text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Matches(word) || Matches(word.Trim('.', ':', '!', '?', '=', '*')))
            {
                return true;
            }

            // key=value or key:value pairs
            var separator = word.IndexOfAny(['=', ':']);
            if (separator > 0 && separator < word.Length - 1 && Matches(word[(separator + 1)..].TrimEnd('.', ',')))
            {
                return true;
            }
        }

        return false;

        bool Matches(string candidate) => candidate.Length >= MinSecretLength &&
                                          secretFingerprints.Contains(SecretFingerprint.Compute(candidate));
    }
}

/// <summary>The fingerprint of a secret value: lower-case hex SHA-256 of its UTF-8 bytes.</summary>
public static class SecretFingerprint
{
    /// <summary>Computes the fingerprint.</summary>
    /// <param name="value">The value.</param>
    public static string Compute(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
