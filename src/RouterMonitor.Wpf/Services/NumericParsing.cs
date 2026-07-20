using System.Globalization;
using System.Text.RegularExpressions;

namespace RouterMonitor.Wpf.Services;

/// <summary>Pulls the leading number out of values like "38093 Kbps" or "3d 1h 52m 17s".</summary>
public static partial class NumericParsing
{
    [GeneratedRegex(@"-?\d+(?:[.,]\d+)?")]
    private static partial Regex LeadingNumberRegex();

    public static double? ExtractLeadingNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = LeadingNumberRegex().Match(text);
        if (!match.Success)
            return null;

        var normalized = match.Value.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
