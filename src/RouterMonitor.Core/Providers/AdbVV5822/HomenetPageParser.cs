using System.Text.RegularExpressions;
using HtmlAgilityPack;
using RouterMonitor.Core.Parsing;

namespace RouterMonitor.Core.Providers.AdbVV5822;

/// <summary>
/// Parses /ui/dboard/homenet device blocks, including the "hostid" the router uses to
/// identify a device for the delete action (<c>?action=delhost&amp;hostid=N</c>). Each device
/// panel is a <c>&lt;div class="homenetInfo" id="tipHostN"&gt;</c> — the id's numeric suffix
/// *is* the hostid, present for every device (active or not), whereas the delete link itself
/// only renders for currently-inactive devices — so reading it off the wrapper div id is more
/// reliable than scraping the link.
/// </summary>
internal static class HomenetPageParser
{
    private static readonly Regex HostIdRegex = new(@"tipHost(\d+)", RegexOptions.Compiled);
    private const string DeviceBlockXPath = ".//div[contains(concat(' ', normalize-space(@class), ' '), ' homenetInfo ')]";
    private const string PanelLabelXPath = ".//label[contains(concat(' ', normalize-space(@class), ' '), ' panel ')]";

    public static IReadOnlyList<HomenetDeviceBlock> ParseDeviceBlocks(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var blocks = new List<HomenetDeviceBlock>();
        foreach (var div in doc.DocumentNode.SelectNodes(DeviceBlockXPath) ?? Enumerable.Empty<HtmlNode>())
        {
            var idAttr = div.GetAttributeValue("id", string.Empty);
            var match = HostIdRegex.Match(idAttr);
            int? hostId = match.Success ? int.Parse(match.Groups[1].Value) : null;

            var titleNode = div.SelectSingleNode(PanelLabelXPath);
            var title = titleNode is not null
                ? HtmlEntity.DeEntitize(titleNode.InnerText).Trim()
                : "Informacje o urządzeniu";

            var fields = InfoFieldParser.ParseInfoFields(div);
            blocks.Add(new HomenetDeviceBlock(hostId, new InfoSection(title, fields)));
        }

        return blocks;
    }
}

internal sealed record HomenetDeviceBlock(int? HostId, InfoSection Section);
