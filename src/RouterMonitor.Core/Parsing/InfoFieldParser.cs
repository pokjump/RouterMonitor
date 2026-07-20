using HtmlAgilityPack;

namespace RouterMonitor.Core.Parsing;

/// <summary>
/// Generic parser for the ADB "epicentro" panel markup. A field looks like:
///   &lt;div class="infoField"&gt;&lt;label&gt;Etykieta:&lt;/label&gt;&lt;span class="text"&gt;wartość&lt;/span&gt;&lt;/div&gt;
/// Confirmed against the live firmware (VV5822_NETIA_7.6.0.0010): /ui/dboard uses class
/// "infoField" with a "span.text" value; /ui/dboard/homenet uses class "formField" for the
/// exact same label/value shape; the ethernet-ports/phone-lines fields use a "div.text"
/// value container instead of "span.text". All three are treated as equivalent here.
///
/// Two distinct kinds of section header exist in the real markup:
///  - &lt;label class="panel"&gt;Nazwa&lt;/label&gt;, a standalone heading for a whole page
///    area (e.g. "Urządzenie", "Serwisy", or one "Informacje o urządzeniu" block per device
///    on the homenet page). Note that the *wrapping* div/img/hr around such a heading often
///    also carries a "panel" class token — only the &lt;label&gt; element counts as a header.
///  - A field whose own &lt;label&gt; carries an additional "title" class, e.g.
///    &lt;div class="infoField"&gt;&lt;label class="title"&gt;Połączenie DSL:&lt;/label&gt;...
///    This both contributes its own label/value pair *and* starts a new sub-section — the
///    dboard overview page packs WAN/DSL/WiFi-2.4/WiFi-5/LAN/ports/phone-lines all inside one
///    "Urządzenie" panel, separated only by these title-labeled fields and &lt;hr/&gt;s.
/// </summary>
public static class InfoFieldParser
{
    private const string FieldXPath =
        ".//div[contains(concat(' ', normalize-space(@class), ' '), ' infoField ') " +
        "or contains(concat(' ', normalize-space(@class), ' '), ' formField ')]";

    private const string ValueXPath =
        ".//*[self::span or self::div][contains(concat(' ', normalize-space(@class), ' '), ' text ')]";

    private const string SectionScanXPath =
        ".//*[(self::label and contains(concat(' ', normalize-space(@class), ' '), ' panel ')) " +
        "or (self::div and (contains(concat(' ', normalize-space(@class), ' '), ' infoField ') " +
        "or contains(concat(' ', normalize-space(@class), ' '), ' formField ')))]";

    private const string NoSectionTitle = "(brak sekcji)";

    /// <summary>Parses every field in the document into a flat label -> value dictionary.</summary>
    public static Dictionary<string, string> ParseInfoFields(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return ParseInfoFields(doc.DocumentNode);
    }

    /// <summary>Parses every field found under the given scope node.</summary>
    public static Dictionary<string, string> ParseInfoFields(HtmlNode scope)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in scope.SelectNodes(FieldXPath) ?? Enumerable.Empty<HtmlNode>())
        {
            var (label, value, _) = ExtractField(field);
            if (label is null)
                continue;

            // Later duplicate labels overwrite earlier ones (e.g. "SSID" appears once per WiFi
            // band on the overview page) — use ParseSections when that ambiguity matters.
            result[label] = value;
        }

        return result;
    }

    private static (string? Label, string Value, bool IsTitleField) ExtractField(HtmlNode field)
    {
        var labelNode = field.SelectSingleNode(".//label");
        var valueNode = field.SelectSingleNode(ValueXPath);

        var label = labelNode?.InnerText is { } raw ? CleanLabel(raw) : null;
        var value = valueNode?.InnerText is { } v ? HtmlEntity.DeEntitize(v).Trim() : string.Empty;
        var isTitleField = labelNode is not null && HasClass(labelNode, "title");
        return (label, value, isTitleField);
    }

    private static string CleanLabel(string raw)
    {
        var text = HtmlEntity.DeEntitize(raw).Trim();
        return text.TrimEnd(':', ' ').Trim();
    }

    /// <summary>
    /// Splits the document into sections. A new section starts at each standalone
    /// &lt;label class="panel"&gt; header, and also at each field whose own label carries a
    /// "title" class (that field's label/value pair becomes the first entry of the new
    /// section). Fields appearing before any header are grouped under "(brak sekcji)" so
    /// nothing is silently dropped.
    /// </summary>
    public static IReadOnlyList<InfoSection> ParseSections(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var sections = new List<InfoSection>();
        var currentTitle = NoSectionTitle;
        var currentFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hasPendingSection = false;

        void Flush()
        {
            if (hasPendingSection)
                sections.Add(new InfoSection(currentTitle, currentFields));
        }

        foreach (var node in doc.DocumentNode.SelectNodes(SectionScanXPath) ?? Enumerable.Empty<HtmlNode>())
        {
            if (node.Name == "label")
            {
                Flush();
                currentTitle = HtmlEntity.DeEntitize(node.InnerText).Trim();
                currentFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                hasPendingSection = true;
                continue;
            }

            var (label, value, isTitleField) = ExtractField(node);
            if (label is null)
                continue;

            if (isTitleField)
            {
                Flush();
                currentTitle = label;
                currentFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            currentFields[label] = value;
            hasPendingSection = true;
        }

        Flush();

        return sections;
    }

    private static bool HasClass(HtmlNode node, string className)
    {
        var classAttr = node.GetAttributeValue("class", string.Empty);
        return $" {classAttr} ".Contains($" {className} ", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A logical section of the dashboard, e.g. "Połączenie DSL", "WiFi-1.1 (2.4GHz)", or one device on the homenet page.</summary>
public sealed record InfoSection(string Title, IReadOnlyDictionary<string, string> Fields);
