using System.Globalization;
using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Backend.McpServices.Services.WorkloadResources;

/// <summary>
///     Parses Kubernetes resource quantities (<c>250m</c>, <c>0.5</c>, <c>512Mi</c>, <c>1Gi</c>, <c>128974848</c>,
///     <c>129e6</c>) into a comparable number (AB#7429). Same suffix rules as the Kubernetes <c>resource.Quantity</c>
///     type: binary (<c>Ki</c> … <c>Ei</c>), decimal (<c>n</c>, <c>u</c>, <c>m</c>, <c>k</c>, <c>M</c> … <c>E</c>) and
///     decimal exponent (<c>e3</c>, <c>E-3</c>). Negative values are not valid resource sizes.
/// </summary>
internal static partial class KubernetesQuantity
{
    private static readonly Dictionary<string, decimal> Multipliers = new(StringComparer.Ordinal)
    {
        [""] = 1m,
        ["n"] = 0.000000001m,
        ["u"] = 0.000001m,
        ["m"] = 0.001m,
        ["k"] = 1_000m,
        ["M"] = 1_000_000m,
        ["G"] = 1_000_000_000m,
        ["T"] = 1_000_000_000_000m,
        ["P"] = 1_000_000_000_000_000m,
        ["E"] = 1_000_000_000_000_000_000m,
        ["Ki"] = 1024m,
        ["Mi"] = 1024m * 1024m,
        ["Gi"] = 1024m * 1024m * 1024m,
        ["Ti"] = 1024m * 1024m * 1024m * 1024m,
        ["Pi"] = 1024m * 1024m * 1024m * 1024m * 1024m,
        ["Ei"] = 1024m * 1024m * 1024m * 1024m * 1024m * 1024m
    };

    /// <summary>Tries to parse <paramref name="text" /> into a number of base units (cores or bytes).</summary>
    public static bool TryParse(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var match = QuantityPattern().Match(text);
        if (!match.Success)
        {
            return false;
        }

        if (!decimal.TryParse(match.Groups["number"].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        var suffix = match.Groups["suffix"].Value;
        try
        {
            if (suffix.Length > 0 && (suffix[0] == 'e' || suffix[0] == 'E') && suffix.Length > 1)
            {
                var exponent = int.Parse(suffix[1..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                if (exponent is < -9 or > 18)
                {
                    return false;
                }

                value = number * (decimal)Math.Pow(10, exponent);
                return true;
            }

            if (!Multipliers.TryGetValue(suffix, out var multiplier))
            {
                return false;
            }

            value = number * multiplier;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^(?<number>[0-9]+(\.[0-9]*)?|\.[0-9]+)(?<suffix>Ki|Mi|Gi|Ti|Pi|Ei|n|u|m|k|M|G|T|P|E|[eE][+-]?[0-9]+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex QuantityPattern();
}
