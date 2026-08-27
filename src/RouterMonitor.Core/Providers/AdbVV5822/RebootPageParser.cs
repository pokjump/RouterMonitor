using HtmlAgilityPack;

namespace RouterMonitor.Core.Providers.AdbVV5822;

/// <summary>
/// Reads the per-request <c>action__key</c> token off /ui/dboard/system/reboot - the reboot
/// form has no password-style hashing, but this token still changes per page load and must be
/// echoed back on submit, so it's read fresh rather than hardcoded.
/// </summary>
internal static class RebootPageParser
{
    public static string ParseActionKey(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var node = doc.DocumentNode.SelectSingleNode("//input[@name='action__key']")
            ?? throw new InvalidOperationException(
                "Nie znaleziono pola 'action__key' na stronie restartu - firmware mógł się zmienić. " +
                "Sprawdź surowy zrzut HTML strony /ui/dboard/system/reboot (tryb surowego dumpu).");

        return HtmlEntity.DeEntitize(node.GetAttributeValue("value", string.Empty));
    }
}
