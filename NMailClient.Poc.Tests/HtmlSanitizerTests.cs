using System.Text.RegularExpressions;
using NMailClient.Poc.Services;
using Xunit;

namespace NMailClient.Poc.Tests;

public class HtmlSanitizerTests
{
    // ---- Externe Bilder ----------------------------------------------------

    [Theory]
    [InlineData("<img src=\"http://tracker.example/pixel.gif\">")]
    [InlineData("<img src='https://tracker.example/pixel.gif'>")]
    [InlineData("<IMG WIDTH=1 SRC=\"https://tracker.example/p.gif\" HEIGHT=1>")]
    public void BlocksRemoteImages(string html)
    {
        var (result, blocked) = HtmlSanitizer.Sanitize(html, allowRemoteImages: false);

        Assert.True(blocked);
        // src muss geleert sein – sonst lädt die Anzeige das Bild trotzdem.
        Assert.Contains("src=\"\"", result);
        Assert.Contains("data-blocked-src", result);

        // Kein *aktives* src mehr. Das gemerkte data-blocked-src enthält die URL
        // absichtlich weiter und muss vor der Prüfung ausgeklammert werden –
        // sonst matcht es sich selbst ("data-blocked-src=" endet auf "src=").
        var withoutMemo = Regex.Replace(result, "data-blocked-src=\"[^\"]*\"", "");
        Assert.DoesNotContain("src=\"http", withoutMemo);
        Assert.DoesNotContain("src='http", withoutMemo);
    }

    [Fact]
    public void KeepsOriginalUrlForLaterUnblocking()
    {
        var (result, _) = HtmlSanitizer.Sanitize(
            "<img src=\"https://example.org/bild.png\">", allowRemoteImages: false);

        // Die URL muss erhalten bleiben, sonst kann "Bilder anzeigen" sie nicht einsetzen.
        Assert.Contains("data-blocked-src=\"https://example.org/bild.png\"", result);
        Assert.Contains("src=\"\"", result);
    }

    [Fact]
    public void AllowsRemoteImagesWhenPermitted()
    {
        var (result, blocked) = HtmlSanitizer.Sanitize(
            "<img src=\"https://example.org/bild.png\">", allowRemoteImages: true);

        Assert.False(blocked);
        Assert.Contains("src=\"https://example.org/bild.png\"", result);
        Assert.DoesNotContain("data-blocked-src", result);
    }

    [Fact]
    public void DoesNotBlockInlineDataImages()
    {
        // cid:/data:-Bilder stecken in der Mail selbst und verraten nichts nach aussen.
        var (_, blocked) = HtmlSanitizer.Sanitize(
            "<img src=\"data:image/png;base64,iVBORw0KGgo=\">", allowRemoteImages: false);

        Assert.False(blocked);
    }

    [Fact]
    public void BlocksRemoteBackgroundImagesInStyles()
    {
        var (result, blocked) = HtmlSanitizer.Sanitize(
            "<div style=\"background: url('https://tracker.example/bg.png')\">x</div>",
            allowRemoteImages: false);

        Assert.True(blocked);
        Assert.Contains("url(blocked:", result);
    }

    // ---- Aktive Inhalte ----------------------------------------------------

    [Fact]
    public void StripsScriptTags()
    {
        var (result, _) = HtmlSanitizer.Sanitize(
            "<p>vorher</p><script>alert('x')</script><p>nachher</p>", allowRemoteImages: true);

        Assert.DoesNotContain("alert", result);
        Assert.DoesNotContain("<script", result);
        Assert.Contains("vorher", result);
        Assert.Contains("nachher", result);
    }

    [Fact]
    public void StripsMultilineScriptBlocks()
    {
        var html = "<script type=\"text/javascript\">\n  var a = 1;\n  alert(a);\n</script><p>ok</p>";
        var (result, _) = HtmlSanitizer.Sanitize(html, allowRemoteImages: true);

        Assert.DoesNotContain("alert", result);
        Assert.Contains("ok", result);
    }

    [Theory]
    [InlineData("<div onclick=\"steal()\">x</div>")]
    [InlineData("<body onload='steal()'>x</body>")]
    [InlineData("<img src=\"data:x\" onerror=\"steal()\">")]
    public void StripsEventHandlerAttributes(string html)
    {
        var (result, _) = HtmlSanitizer.Sanitize(html, allowRemoteImages: true);
        Assert.DoesNotContain("steal", result);
    }

    // ---- Rahmen ------------------------------------------------------------

    [Fact]
    public void EmitsUtf8AndKeepsBodyContent()
    {
        var (result, _) = HtmlSanitizer.Sanitize("<p>Grüße & Umlaute</p>", allowRemoteImages: true);

        Assert.Contains("charset=\"utf-8\"", result);
        Assert.Contains("Grüße & Umlaute", result);
    }

    [Fact]
    public void FromPlainTextEscapesMarkup()
    {
        // Reintext darf nicht als Markup interpretiert werden.
        var result = HtmlSanitizer.FromPlainText("<script>alert(1)</script> & <b>fett</b>");

        Assert.DoesNotContain("<script", result);
        Assert.Contains("&lt;script&gt;", result);
        Assert.Contains("&lt;b&gt;fett&lt;/b&gt;", result);
        Assert.Contains("&amp;", result);
    }

    [Fact]
    public void FromPlainTextPreservesLineBreaks()
    {
        var result = HtmlSanitizer.FromPlainText("Zeile 1\nZeile 2");

        Assert.Contains("<pre>", result);
        Assert.Contains("Zeile 1\nZeile 2", result);
    }

    [Fact]
    public void HandlesEmptyInput()
    {
        var (result, blocked) = HtmlSanitizer.Sanitize("", allowRemoteImages: false);

        Assert.False(blocked);
        Assert.Contains("<body>", result);
    }
}
