using System.Windows;
using System.Windows.Documents;
using NMailClient.Poc.Services;
using Xunit;

namespace NMailClient.Poc.Tests;

/// <summary>
/// Der FlowDocument→HTML-Konverter ist die bekannte Schwachstelle des
/// RichTextBox-Wegs (siehe ROADMAP 0.4.0). Deshalb je Konstrukt ein Test.
/// </summary>
public class FlowDocumentHtmlTests
{
    private static FlowDocument Doc(params Block[] blocks)
    {
        var d = new FlowDocument();
        foreach (var b in blocks) d.Blocks.Add(b);
        return d;
    }

    private static Paragraph P(params Inline[] inlines)
    {
        var p = new Paragraph();
        foreach (var i in inlines) p.Inlines.Add(i);
        return p;
    }

    // ---- Absätze und Text ---------------------------------------------------

    [Fact]
    public void PlainParagraphBecomesP()
        => Assert.Equal($"<p style=\"{FlowDocumentHtml.ParagraphStyle}\">Hallo</p>", FlowDocumentHtml.ToHtml(Doc(P(new Run("Hallo")))));

    [Fact]
    public void EmptyParagraphKeepsTheBlankLine()
        => Assert.Equal($"<p style=\"{FlowDocumentHtml.ParagraphStyle}\"><br></p>", FlowDocumentHtml.ToHtml(Doc(new Paragraph())));

    [Fact]
    public void LineBreakBecomesBr()
    {
        var html = FlowDocumentHtml.ToHtml(Doc(P(new Run("a"), new LineBreak(), new Run("b"))));
        Assert.Equal($"<p style=\"{FlowDocumentHtml.ParagraphStyle}\">a<br>b</p>", html);
    }

    [Fact]
    public void EscapesMarkupInText()
    {
        var html = FlowDocumentHtml.ToHtml(Doc(P(new Run("<script>alert(1)</script> & \"x\""))));

        Assert.DoesNotContain("<script", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;", html);
        Assert.Contains("&quot;", html);
    }

    [Fact]
    public void KeepsUmlautsAsIs()
        => Assert.Contains("Grüße", FlowDocumentHtml.ToHtml(Doc(P(new Run("Grüße")))));

    // ---- Auszeichnungen -----------------------------------------------------

    [Fact]
    public void BoldViaFontWeightProperty()
    {
        // So setzt EditingCommands.ToggleBold die Auszeichnung – nicht als <Bold>.
        var run = new Run("fett") { FontWeight = FontWeights.Bold };
        Assert.Equal($"<p style=\"{FlowDocumentHtml.ParagraphStyle}\"><strong>fett</strong></p>", FlowDocumentHtml.ToHtml(Doc(P(run))));
    }

    [Fact]
    public void ItalicViaFontStyleProperty()
    {
        var run = new Run("kursiv") { FontStyle = FontStyles.Italic };
        Assert.Equal($"<p style=\"{FlowDocumentHtml.ParagraphStyle}\"><em>kursiv</em></p>", FlowDocumentHtml.ToHtml(Doc(P(run))));
    }

    [Fact]
    public void UnderlineAndStrikethrough()
    {
        var u = new Run("u") { TextDecorations = TextDecorations.Underline };
        var s = new Run("s") { TextDecorations = TextDecorations.Strikethrough };

        Assert.Contains("<u>u</u>", FlowDocumentHtml.ToHtml(Doc(P(u))));
        Assert.Contains("<s>s</s>", FlowDocumentHtml.ToHtml(Doc(P(s))));
    }

    [Fact]
    public void CombinedFormattingNests()
    {
        var run = new Run("x") { FontWeight = FontWeights.Bold, FontStyle = FontStyles.Italic };
        var html = FlowDocumentHtml.ToHtml(Doc(P(run)));

        Assert.Contains("<strong>", html);
        Assert.Contains("<em>", html);
        // Tags müssen sauber geschachtelt schliessen.
        Assert.EndsWith("</em></strong></p>", html);
    }

    [Fact]
    public void UnformattedTextGetsNoTags()
    {
        var html = FlowDocumentHtml.ToHtml(Doc(P(new Run("schlicht"))));

        Assert.DoesNotContain("<strong>", html);
        Assert.DoesNotContain("<span", html);
    }

    [Fact]
    public void FontSizeIsEmittedOnlyWhenItDiffers()
    {
        var normal = new Run("a") { FontSize = 14 };
        var big = new Run("b") { FontSize = 20 };

        Assert.DoesNotContain("font-size", FlowDocumentHtml.ToHtml(Doc(P(normal))));
        Assert.Contains("font-size:15pt", FlowDocumentHtml.ToHtml(Doc(P(big))));
    }

    // ---- Listen -------------------------------------------------------------

    [Fact]
    public void BulletListBecomesUl()
    {
        var list = new List();
        list.ListItems.Add(new ListItem(P(new Run("eins"))));
        list.ListItems.Add(new ListItem(P(new Run("zwei"))));

        Assert.Equal("<ul><li>eins</li><li>zwei</li></ul>", FlowDocumentHtml.ToHtml(Doc(list)));
    }

    [Fact]
    public void NumberedListBecomesOl()
    {
        var list = new List { MarkerStyle = TextMarkerStyle.Decimal };
        list.ListItems.Add(new ListItem(P(new Run("eins"))));

        Assert.StartsWith("<ol>", FlowDocumentHtml.ToHtml(Doc(list)));
    }

    [Fact]
    public void ListItemsCarryTheirFormatting()
    {
        var list = new List();
        list.ListItems.Add(new ListItem(P(new Run("fett") { FontWeight = FontWeights.Bold })));

        Assert.Contains("<li><strong>fett</strong></li>", FlowDocumentHtml.ToHtml(Doc(list)));
    }

    [Fact]
    public void NestedListsSurvive()
    {
        var inner = new List();
        inner.ListItems.Add(new ListItem(P(new Run("innen"))));

        var outerItem = new ListItem(P(new Run("aussen")));
        outerItem.Blocks.Add(inner);

        var outer = new List();
        outer.ListItems.Add(outerItem);

        var html = FlowDocumentHtml.ToHtml(Doc(outer));

        Assert.Contains("aussen", html);
        Assert.Contains("<ul><li>innen</li></ul>", html);
    }

    // ---- Zitate und Links ---------------------------------------------------

    [Fact]
    public void SectionBecomesBlockquote()
    {
        var section = new Section();
        section.Blocks.Add(P(new Run("zitiert")));

        Assert.Equal($"<blockquote><p style=\"{FlowDocumentHtml.ParagraphStyle}\">zitiert</p></blockquote>",
            FlowDocumentHtml.ToHtml(Doc(section)));
    }

    [Fact]
    public void HyperlinkKeepsTarget()
    {
        var link = new Hyperlink(new Run("Klick")) { NavigateUri = new Uri("https://example.org/a") };

        Assert.Contains("<a href=\"https://example.org/a\">Klick</a>",
            FlowDocumentHtml.ToHtml(Doc(P(link))));
    }

    [Fact]
    public void HyperlinkWithUmlautsInTargetIsEscapedNotBroken()
    {
        var link = new Hyperlink(new Run("Test"))
        {
            NavigateUri = new Uri("https://example.org/grüße?a=1&b=2"),
        };
        var html = FlowDocumentHtml.ToHtml(Doc(P(link)));

        // & im Ziel muss maskiert sein, sonst zerfällt das Attribut.
        Assert.Contains("&amp;b=2", html);
        Assert.DoesNotContain("\"&b", html);
    }

    // ---- Textfassung --------------------------------------------------------

    [Fact]
    public void PlainTextKeepsParagraphsAsLines()
    {
        var text = FlowDocumentHtml.ToPlainText(
            Doc(P(new Run("eins")), P(new Run("zwei"))));

        Assert.Equal("eins\nzwei", text);
    }

    [Fact]
    public void PlainTextMarksListsAndQuotes()
    {
        var list = new List();
        list.ListItems.Add(new ListItem(P(new Run("punkt"))));

        var section = new Section();
        section.Blocks.Add(P(new Run("zitat")));

        Assert.Contains("- punkt", FlowDocumentHtml.ToPlainText(Doc(list)));
        Assert.Contains("> zitat", FlowDocumentHtml.ToPlainText(Doc(section)));
    }

    [Fact]
    public void PlainTextContainsNoMarkup()
    {
        var run = new Run("fett") { FontWeight = FontWeights.Bold };
        var text = FlowDocumentHtml.ToPlainText(Doc(P(run)));

        Assert.Equal("fett", text);
        Assert.DoesNotContain("<", text);
    }

    [Fact]
    public void EmptyDocumentYieldsEmptyOutput()
    {
        Assert.Equal("", FlowDocumentHtml.ToHtml(new FlowDocument()));
        Assert.Equal("", FlowDocumentHtml.ToPlainText(new FlowDocument()));
    }
}
