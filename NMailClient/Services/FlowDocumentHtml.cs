using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace NMailClient.Services;

/// <summary>
/// Wandelt das <see cref="FlowDocument"/> des Composers in HTML.
///
/// WPF bietet dafür nichts an – <c>TextRange.Save</c> kann nur XAML und RTF.
/// Deshalb bewusst **kein** allgemeiner XAML→HTML-Übersetzer, sondern nur die
/// Konstrukte, die die Formatierleiste erzeugen kann: Fett, Kursiv, Unterstrichen,
/// Durchgestrichen, Listen, Zitat, Link, Schriftgröße. Alles andere fällt auf
/// reinen Text zurück, statt fehlerhaftes Markup zu erzeugen.
/// </summary>
public static class FlowDocumentHtml
{
    /// <summary>Grundschriftgröße; nur Abweichungen werden ausgezeichnet.</summary>
    private const double BaseFontSize = 14.0;

    public static string ToHtml(FlowDocument doc)
    {
        var sb = new StringBuilder();
        foreach (var block in doc.Blocks) WriteBlock(block, sb);
        return sb.ToString();
    }

    /// <summary>
    /// Textfassung für multipart/alternative. Empfänger ohne HTML sollen etwas
    /// Lesbares bekommen, nicht Markup im Klartext.
    /// </summary>
    public static string ToPlainText(FlowDocument doc)
    {
        var sb = new StringBuilder();
        foreach (var block in doc.Blocks) WritePlainBlock(block, sb, "");
        return sb.ToString().TrimEnd('\n');
    }

    // ---- HTML ---------------------------------------------------------------

    /// <summary>
    /// Der Absatzabstand, den auch der Verfasser anzeigt (siehe die
    /// Paragraph-Vorlage in ComposeWindow.xaml).
    ///
    /// Bewusst als <b>eingebettete</b> Angabe an jedem Absatz und nicht als
    /// <c>&lt;style&gt;</c>-Block im Kopf: den entfernen viele Mailprogramme,
    /// und die Mail sähe beim Empfänger dann anders aus als beim Schreiben.
    /// Ohne die Angabe gälte der Vorgabewert des Anzeigeprogramms — bei den
    /// meisten rund die doppelte Höhe.
    /// </summary>
    public const string ParagraphStyle = "margin:0 0 8px 0";

    private const string ParagraphOpen = $"<p style=\"{ParagraphStyle}\">";

    private static void WriteBlock(Block block, StringBuilder sb)
    {
        switch (block)
        {
            case Paragraph p:
                // Leere Absätze als <p><br></p>, sonst schluckt HTML die Leerzeile.
                sb.Append(ParagraphOpen);
                var before = sb.Length;
                foreach (var inline in p.Inlines) WriteInline(inline, sb);
                if (sb.Length == before) sb.Append("<br>");
                sb.Append("</p>");
                break;

            case List list:
                var tag = list.MarkerStyle is TextMarkerStyle.Decimal
                    or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin
                    or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman
                    ? "ol" : "ul";

                sb.Append('<').Append(tag).Append('>');
                foreach (var item in list.ListItems)
                {
                    sb.Append("<li>");
                    foreach (var b in item.Blocks) WriteListItemBlock(b, sb);
                    sb.Append("</li>");
                }
                sb.Append("</").Append(tag).Append('>');
                break;

            case Section section:
                // Section entsteht beim Einrücken – als Zitat ausgeben.
                sb.Append("<blockquote>");
                foreach (var b in section.Blocks) WriteBlock(b, sb);
                sb.Append("</blockquote>");
                break;

            default:
                // Unbekannter Blocktyp: Inhalt als Absatz retten, nichts erfinden.
                sb.Append(ParagraphOpen).Append(Escape(PlainOf(block))).Append("</p>");
                break;
        }
    }

    /// <summary>In Listenpunkten ohne umschliessendes &lt;p&gt;, das erzeugt Leerraum.</summary>
    private static void WriteListItemBlock(Block block, StringBuilder sb)
    {
        if (block is Paragraph p)
            foreach (var inline in p.Inlines) WriteInline(inline, sb);
        else
            WriteBlock(block, sb);
    }

    private static void WriteInline(Inline inline, StringBuilder sb)
    {
        switch (inline)
        {
            case LineBreak:
                sb.Append("<br>");
                return;

            case Hyperlink link:
            {
                var href = link.NavigateUri?.ToString() ?? TextOf(link);
                sb.Append("<a href=\"").Append(Escape(href)).Append("\">");
                var (open, close) = Decorate(link);
                sb.Append(open);
                foreach (var child in link.Inlines) WriteInline(child, sb);
                sb.Append(close).Append("</a>");
                return;
            }

            case Span span:
            {
                var (open, close) = Decorate(span);
                sb.Append(open);
                foreach (var child in span.Inlines) WriteInline(child, sb);
                sb.Append(close);
                return;
            }

            case Run run:
            {
                var (open, close) = Decorate(run);
                sb.Append(open).Append(Escape(run.Text)).Append(close);
                return;
            }

            default:
                sb.Append(Escape(TextOf(inline)));
                return;
        }
    }

    /// <summary>
    /// Auszeichnung aus den Eigenschaften ableiten, nicht aus dem Typ: die
    /// WPF-Editierbefehle setzen FontWeight/FontStyle auf einem Span, statt
    /// Bold/Italic-Elemente zu erzeugen.
    /// </summary>
    private static (string Open, string Close) Decorate(TextElement element)
    {
        var open = new StringBuilder();
        var close = new StringBuilder();

        void Wrap(string tag)
        {
            open.Append('<').Append(tag).Append('>');
            close.Insert(0, $"</{tag}>");
        }

        if (IsSet(element, TextElement.FontWeightProperty)
            && element.FontWeight >= FontWeights.Bold) Wrap("strong");

        if (IsSet(element, TextElement.FontStyleProperty)
            && element.FontStyle == FontStyles.Italic) Wrap("em");

        // Hyperlinks ausnehmen: deren Unterstreichung kommt aus dem Standardstil,
        // nicht vom Nutzer – ein <u> in <a> wäre doppelt gemoppelt.
        if (element is Inline and not Hyperlink
            && ((Inline)element).TextDecorations is { Count: > 0 } decorations)
        {
            if (Contains(decorations, TextDecorations.Underline)) Wrap("u");
            if (Contains(decorations, TextDecorations.Strikethrough)) Wrap("s");
        }

        if (IsSet(element, TextElement.FontSizeProperty)
            && Math.Abs(element.FontSize - BaseFontSize) > 0.1)
        {
            var pt = Math.Round(element.FontSize * 0.75, 1);
            open.Append("<span style=\"font-size:")
                .Append(pt.ToString(CultureInfo.InvariantCulture)).Append("pt\">");
            close.Insert(0, "</span>");
        }

        return (open.ToString(), close.ToString());
    }

    /// <summary>Nur örtlich gesetzte Werte auszeichnen, keine geerbten.</summary>
    private static bool IsSet(DependencyObject o, DependencyProperty p)
        => DependencyPropertyHelper.GetValueSource(o, p).BaseValueSource
            is not (BaseValueSource.Default or BaseValueSource.Inherited);

    private static bool Contains(TextDecorationCollection collection, TextDecorationCollection what)
        => what.Count > 0 && collection.Any(d => d.Location == what[0].Location);

    // ---- Text ---------------------------------------------------------------

    private static void WritePlainBlock(Block block, StringBuilder sb, string prefix)
    {
        switch (block)
        {
            case Paragraph p:
                sb.Append(prefix).Append(TextOfParagraph(p)).Append('\n');
                break;

            case List list:
            {
                bool numbered = list.MarkerStyle == TextMarkerStyle.Decimal;
                int n = 1;
                foreach (var item in list.ListItems)
                {
                    var marker = numbered ? $"{n++}. " : "- ";
                    foreach (var b in item.Blocks)
                        WritePlainBlock(b, sb, prefix + marker);
                }
                break;
            }

            case Section section:
                foreach (var b in section.Blocks) WritePlainBlock(b, sb, prefix + "> ");
                break;

            default:
                sb.Append(prefix).Append(PlainOf(block)).Append('\n');
                break;
        }
    }

    private static string TextOfParagraph(Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var inline in p.Inlines)
        {
            if (inline is LineBreak) sb.Append('\n');
            else sb.Append(TextOf(inline));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Für Runs den Text direkt nehmen: ein TextRange über einen Run innerhalb
    /// eines Listenpunkts liefert das Aufzählungszeichen gleich mit („•\tpunkt").
    /// </summary>
    private static string TextOf(TextElement element)
        => element is Run run
            ? run.Text
            : new TextRange(element.ContentStart, element.ContentEnd).Text;

    private static string PlainOf(Block block)
        => new TextRange(block.ContentStart, block.ContentEnd).Text;

    private static string Escape(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;");
}
