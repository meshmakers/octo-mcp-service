namespace Meshmakers.Octo.Backend.McpServices.Services.Knowledge;

/// <summary>
///     AB#8566: a light English stemmer for scoring knowledge search hits. The page text index of MongoDB stems English
///     words (Snowball), so "pipelines" finds a page that says "pipeline"; the tool's own score must count such a hit too,
///     or the score-0 filter drops a real index match. This stemmer covers the regular inflections (plural -s/-es/-ies,
///     -ing, -ed, final -e and -y, doubled final consonant, short verbs such as used/using -> use, but not being -> bee); it does not reproduce every Snowball rule (derivations such as
///     -ness, -ation are not reduced).
/// </summary>
internal static class KnowledgeStemmer
{
    private const int MinStemLength = 3;

    /// <summary>Returns the stem of a lower-case word; words shorter than 4 characters are returned unchanged.</summary>
    public static string Stem(string word)
    {
        if (word.Length <= MinStemLength)
        {
            return word;
        }

        var stem = StripInflection(word);

        // need/needs/needed -> nee, agree/agreed -> agre (below): an "eed" ending loses its "d".
        if (stem.EndsWith("eed", StringComparison.Ordinal))
        {
            stem = stem[..^1];
        }

        // Normalise the ending the same way for every form: configure/configured -> configur, policy/policies -> polici.
        if (stem.Length > MinStemLength && stem[^1] == 'e')
        {
            stem = stem[..^1];
        }
        else if (stem.Length > MinStemLength && stem[^1] == 'y')
        {
            stem = stem[..^1] + "i";
        }

        return stem;
    }

    /// <summary>Splits text into lower-case words (letters and digits).</summary>
    public static IEnumerable<string> Words(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isWordChar = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (isWordChar && start < 0)
            {
                start = i;
            }
            else if (!isWordChar && start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }

    private static string StripInflection(string word)
    {
        if (word.EndsWith("ies", StringComparison.Ordinal) && word.Length - 3 >= MinStemLength - 1)
        {
            return word[..^3] + "y";
        }

        // "eed" is not "e" + "ed": speed is not "spe" + "ed" (Stem reduces the "eed" afterwards).
        if (TryStrip(word, "ing", out var stem) ||
            (!word.EndsWith("eed", StringComparison.Ordinal) && TryStrip(word, "ed", out stem)))
        {
            // A two-letter rest of vowel + consonant is a short verb that lost its final "e": used/using -> use, like
            // use/uses (also aged, owed). Other two-letter rests stay: being -> be, doing -> do, eyed -> ey (not bee,
            // doe, eye).
            if (stem.Length < MinStemLength)
            {
                return IsVowel(stem[0]) && stem[0] != 'y' && !IsVowel(stem[1]) ? stem + "e" : stem;
            }

            return UndoubleConsonant(stem);
        }

        if (word.EndsWith("sses", StringComparison.Ordinal) || word.EndsWith("xes", StringComparison.Ordinal) ||
            word.EndsWith("ches", StringComparison.Ordinal) || word.EndsWith("shes", StringComparison.Ordinal))
        {
            return word[..^2];
        }

        if (word[^1] == 's' && word[^2] is not ('s' or 'u' or 'i'))
        {
            return word[..^1];
        }

        return word;
    }

    private static bool TryStrip(string word, string suffix, out string stem)
    {
        stem = word;
        if (!word.EndsWith(suffix, StringComparison.Ordinal) || word.Length - suffix.Length < MinStemLength - 1)
        {
            return false;
        }

        var rest = word[..^suffix.Length];
        if (!rest.Any(IsVowel))
        {
            return false;
        }

        stem = rest;
        return true;
    }

    private static string UndoubleConsonant(string stem) =>
        stem.Length > MinStemLength && stem[^1] == stem[^2] && !IsVowel(stem[^1]) && stem[^1] is not ('l' or 's' or 'z')
            ? stem[..^1]
            : stem;

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';
}
