using System.Text.RegularExpressions;

namespace NMailClient.Services;

/// <summary>
/// Blockiert externe Bilder und aktive Inhalte in HTML-Mails – Pendant zum
/// Bilder-Blocker der Go-Version.
///
/// <para><b>Offener Punkt:</b> die Erkennung arbeitet mit regulaeren Ausdruecken.
/// Fuer fremdes HTML ist das die falsche Werkzeugklasse — jedes Muster hier
/// setzt Markup voraus, das ein Angreifer nicht schreiben muss: <c>OnAttr</c>
/// verlangt gequotete Attributwerte, <c>ScriptTag</c> ein schliessendes Tag,
/// <c>RemoteImg</c> ein ausgeschriebenes <c>http(s):</c>. Ungequotet,
/// unabgeschlossen oder protokollrelativ geht alles davon durch.</para>
///
/// <para>Fuer <b>Skript</b> ist das seit 0.9.1 folgenlos: die Mailansicht laeuft
/// mit <c>IsScriptEnabled = false</c>, siehe <c>MainWindow.SetupWebView</c>.
/// Was bleibt, ist <b>Nachverfolgung</b> — ein <c>&lt;img src=//zaehler/p.gif&gt;</c>,
/// ein <c>&lt;link rel=stylesheet&gt;</c> oder ein <c>srcset</c> laedt trotz
/// blockierter Bilder und verraet dem Absender das Lesen. Das behebt erst ein
/// echter Parser (AngleSharp) mit Positivliste, flankiert von einer
/// Content-Security-Policy im <c>Shell</c>-Kopf.</para>
/// </summary>
public static class HtmlSanitizer
{
    private static readonly RegexOptions Opts =
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;

    private static readonly Regex ScriptTag = new(@"<script\b.*?</script\s*>", Opts);
    private static readonly Regex StyleUrl = new(@"url\s*\(\s*['""]?https?:", Opts);
    private static readonly Regex RemoteImg = new(@"<img\b([^>]*?)\bsrc\s*=\s*(['""])(https?://[^'""]*)\2", Opts);
    private static readonly Regex OnAttr = new(@"\son\w+\s*=\s*(['""]).*?\1", Opts);

    /// <returns>Bereinigtes HTML und ob überhaupt etwas blockiert wurde.</returns>
    public static (string Html, bool BlockedImages) Sanitize(string html, bool allowRemoteImages)
    {
        var s = ScriptTag.Replace(html, "");
        s = OnAttr.Replace(s, "");

        bool blocked = false;
        if (!allowRemoteImages)
        {
            // src merken (data-blocked-src), damit "Bilder anzeigen" sie wieder einsetzen könnte.
            s = RemoteImg.Replace(s, m =>
            {
                blocked = true;
                return $"<img{m.Groups[1].Value} data-blocked-src=\"{m.Groups[3].Value}\" src=\"\"";
            });
            if (StyleUrl.IsMatch(s))
            {
                blocked = true;
                s = StyleUrl.Replace(s, "url(blocked:");
            }
        }

        var shell = Shell
            .Replace("{{BG}}", ThemeManager.HexColor("HtmlBgColor"))
            .Replace("{{FG}}", ThemeManager.HexColor("HtmlFgColor"))
            .Replace("{{SCHEME}}", ThemeManager.IsDark ? "dark" : "light")
            // Mails bringen oft harte helle Hintergründe mit. Im Dark Mode invertieren
            // wir sie behutsam zurück, statt sie weiss leuchten zu lassen.
            .Replace("{{DARKFIX}}", ThemeManager.IsDark ? DarkFix : "");

        return (shell.Replace("{{BODY}}", s), blocked);
    }

    private const string DarkFix = """
          body :where([bgcolor], [style*="background"]) { background-color: transparent !important; }
          body :where(font[color], [style*="color:#000"], [style*="color: #000"]) { color: inherit !important; }
          a { color: #7FB0EE; }
        """;

    private const string Shell = """
        <html><head>
        <meta charset="utf-8">
        <meta name="color-scheme" content="{{SCHEME}}">
        <style>
          html { color-scheme: {{SCHEME}}; }
          body { font-family: Segoe UI, sans-serif; font-size: 13.5px; line-height: 1.5;
                 background: {{BG}}; color: {{FG}};
                 margin: 16px; word-wrap: break-word; }
          img { max-width: 100%; height: auto; }
          pre { white-space: pre-wrap; font-family: Consolas, monospace; }
          blockquote { border-left: 3px solid #888; margin-left: 0; padding-left: 12px;
                       opacity: .8; }
          table { max-width: 100%; }
        {{DARKFIX}}
        </style>
        </head><body>{{BODY}}</body></html>
        """;

    /// <summary>Reintext-Mails als HTML darstellen (Zeilenumbrüche erhalten).</summary>
    public static string FromPlainText(string text)
    {
        var escaped = text
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        return Sanitize($"<pre>{escaped}</pre>", true).Html;
    }
}
