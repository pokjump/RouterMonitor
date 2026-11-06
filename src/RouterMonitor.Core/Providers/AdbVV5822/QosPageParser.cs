using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace RouterMonitor.Core.Providers.AdbVV5822;

/// <summary>
/// Parses the QoS pages under /ui/dboard/settings/routing (policer list, classification list) and
/// reads the default field set of the panel's edit forms, so a POST can resend every field the
/// browser would, with only the ones we care about overridden.
/// </summary>
internal static partial class QosPageParser
{
    public const string SavedMarker = "zapisane poprawnie";

    [GeneratedRegex(@"policersedit\?instance=(\d+)")]
    private static partial Regex PolicerInstanceRegex();

    [GeneratedRegex(@"classes/classification\?instance=(\d+)")]
    private static partial Regex ClassInstanceRegex();

    public static IReadOnlyList<QosPolicerRow> ParsePolicers(string html)
    {
        var rows = new List<QosPolicerRow>();
        foreach (var tr in FormRows(html))
        {
            var cells = tr.Elements("td").ToList();
            var instance = FindInstance(tr, PolicerInstanceRegex());
            if (cells.Count == 0 || instance is null)
                continue;
            rows.Add(new QosPolicerRow(instance.Value, Text(cells[0])));
        }
        return rows;
    }

    public static IReadOnlyList<QosClassRow> ParseClasses(string html)
    {
        var rows = new List<QosClassRow>();
        foreach (var tr in FormRows(html))
        {
            var cells = tr.Elements("td").ToList();
            var instance = FindInstance(tr, ClassInstanceRegex());
            if (cells.Count < 3 || instance is null)
                continue;

            var criteria = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var labelCell in cells[2].SelectNodes(".//td[@class='label']") ?? Enumerable.Empty<HtmlNode>())
            {
                var valueCell = labelCell.NextSibling;
                while (valueCell is not null && valueCell.Name != "td")
                    valueCell = valueCell.NextSibling;
                criteria[Text(labelCell)] = valueCell is null ? "" : Text(valueCell);
            }

            rows.Add(new QosClassRow(instance.Value, Text(cells[1]), criteria));
        }
        return rows;
    }

    /// <summary>
    /// Field values the browser would submit for the page's POST form as rendered: text/hidden
    /// inputs, checked radios and checkboxes, and each select's selected (else first) option.
    /// </summary>
    public static Dictionary<string, string> ReadFormDefaults(string html)
    {
        var doc = Load(html);
        var form = doc.DocumentNode.SelectSingleNode("//form[@method='post']")
            ?? throw new InvalidOperationException("Na stronie routera nie znaleziono formularza do zapisu.");

        var fields = new Dictionary<string, string>();
        foreach (var input in form.SelectNodes(".//input[@name]") ?? Enumerable.Empty<HtmlNode>())
        {
            var name = input.GetAttributeValue("name", "");
            var type = input.GetAttributeValue("type", "text").ToLowerInvariant();
            var value = HtmlEntity.DeEntitize(input.GetAttributeValue("value", ""));
            switch (type)
            {
                case "button" or "submit" or "image" or "reset":
                    break;
                case "radio" or "checkbox":
                    if (input.Attributes.Contains("checked"))
                        fields[name] = type == "checkbox" && value == "" ? "on" : value;
                    break;
                default:
                    fields[name] = value;
                    break;
            }
        }

        foreach (var select in form.SelectNodes(".//select[@name]") ?? Enumerable.Empty<HtmlNode>())
        {
            // HtmlAgilityPack treats <option> as an empty element, so read the options by
            // attribute from the select's descendants rather than relying on their text.
            var options = select.SelectNodes(".//option") ?? Enumerable.Empty<HtmlNode>();
            var chosen = options.FirstOrDefault(o => o.Attributes.Contains("selected")) ?? options.FirstOrDefault();
            fields[select.GetAttributeValue("name", "")] = chosen is null ? "" : HtmlEntity.DeEntitize(chosen.GetAttributeValue("value", ""));
        }

        return fields;
    }

    /// <summary>Value of the &lt;option&gt; whose visible text is <paramref name="text"/> inside select <paramref name="selectName"/>.</summary>
    public static string? FindOptionValue(string html, string selectName, string text)
    {
        var selectMatch = Regex.Match(html, $@"<select name=""{Regex.Escape(selectName)}""[^>]*>(.*?)</select>", RegexOptions.Singleline);
        if (!selectMatch.Success)
            return null;

        var option = Regex.Match(selectMatch.Groups[1].Value,
            $@"<option value=['""]([^'""]*)['""][^>]*>\s*{Regex.Escape(text)}\s*</option>", RegexOptions.Singleline);
        return option.Success ? option.Groups[1].Value : null;
    }

    public static int? ReadIntInput(string html, string name)
    {
        var match = Regex.Match(html, $@"name=""{Regex.Escape(name)}""\s+value=""(\d+)""");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static IEnumerable<HtmlNode> FormRows(string html) =>
        Load(html).DocumentNode.SelectNodes("//table[contains(@class,'formTable')]/tbody/tr[contains(@class,'formRow')]")
        ?? Enumerable.Empty<HtmlNode>();

    private static int? FindInstance(HtmlNode row, Regex regex)
    {
        foreach (var a in row.SelectNodes(".//a[@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            var match = regex.Match(a.GetAttributeValue("href", ""));
            if (match.Success)
                return int.Parse(match.Groups[1].Value);
        }
        return null;
    }

    private static HtmlDocument Load(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc;
    }

    private static string Text(HtmlNode node) => HtmlEntity.DeEntitize(node.InnerText).Trim();
}

internal sealed record QosPolicerRow(int Instance, string Name);

internal sealed record QosClassRow(int Instance, string Name, IReadOnlyDictionary<string, string> Criteria);
