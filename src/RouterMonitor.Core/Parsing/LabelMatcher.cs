namespace RouterMonitor.Core.Parsing;

/// <summary>
/// Looks up parsed fields by matching label *text* against a list of candidate substrings,
/// instead of relying on fixed positions/indexes. This keeps mapping resilient to minor
/// label wording differences (language, punctuation) across firmware pages.
/// </summary>
public static class LabelMatcher
{
    /// <summary>Returns the value of the first field whose label contains any of the candidates (case-insensitive), or null.</summary>
    public static string? FindByAnyContains(IReadOnlyDictionary<string, string> fields, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            foreach (var (label, value) in fields)
            {
                if (label.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                    return value;
            }
        }

        return null;
    }
}
