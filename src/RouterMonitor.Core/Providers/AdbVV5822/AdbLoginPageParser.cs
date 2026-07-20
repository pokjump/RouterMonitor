using HtmlAgilityPack;

namespace RouterMonitor.Core.Providers.AdbVV5822;

/// <summary>
/// Parses the per-request values out of the ADB "epicentro" /ui/login page: a fresh
/// <c>nonce</c> plus three opaque <c>code1</c>/<c>code2</c>/<c>code3</c> tokens that must be
/// echoed back unchanged. Field *names* are hardcoded (confirmed against the live firmware,
/// VV5822_NETIA_7.6.0.0010, which will not change), but their *values* are read fresh from
/// every login page fetch since they're per-session.
/// </summary>
internal static class AdbLoginPageParser
{
    public static AdbLoginPage Parse(string html, Uri baseAddress)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var form = doc.DocumentNode.SelectSingleNode("//form[@name='form']")
            ?? doc.DocumentNode.SelectSingleNode("//form")
            ?? throw new InvalidOperationException(
                "Nie znaleziono formularza logowania na stronie /ui/login. " +
                "Włącz tryb surowego zrzutu HTML i sprawdź rzeczywisty markup.");

        var actionAttr = form.GetAttributeValue("action", "/ui/login");
        var actionUri = new Uri(baseAddress, actionAttr);

        var nonce = RequireInputValue(form, "nonce");
        var code1 = RequireInputValue(form, "code1");
        var code2 = RequireInputValue(form, "code2");
        var code3 = RequireInputValue(form, "code3");

        var language = form.SelectSingleNode(".//select[@name='language']/option[@selected]")
            ?.GetAttributeValue("value", "PL") ?? "PL";

        return new AdbLoginPage(actionUri, nonce, code1, code2, code3, language);
    }

    private static string RequireInputValue(HtmlNode form, string name)
    {
        var node = form.SelectSingleNode($".//input[@name='{name}']");
        if (node is null)
        {
            throw new InvalidOperationException(
                $"Brak oczekiwanego pola '{name}' na stronie logowania — firmware mógł się zmienić. " +
                "Sprawdź surowy zrzut HTML strony /ui/login.");
        }

        return HtmlEntity.DeEntitize(node.GetAttributeValue("value", string.Empty));
    }
}

internal sealed record AdbLoginPage(Uri Action, string Nonce, string Code1, string Code2, string Code3, string Language);
