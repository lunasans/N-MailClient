using System.Text;

namespace NMailClient.Services.Sieve;

/// <summary>Abwesenheitsnotiz nach RFC 5230.</summary>
public sealed class VacationSettings
{
    public bool Enabled { get; set; }

    /// <summary>Wie viele Tage lang derselbe Absender höchstens eine Antwort bekommt.</summary>
    public int Days { get; set; } = 7;

    public string Subject { get; set; } = "Abwesend";
    public string Message { get; set; } = "";

    /// <summary>
    /// Eigene Adressen. Ohne die antwortet der Server auch auf Post, die nur
    /// über einen Verteiler kam — was einen ganzen Verteiler zumüllt.
    /// </summary>
    public List<string> Addresses { get; set; } = [];

    public string? Problem
    {
        get
        {
            if (!Enabled) return null;
            if (string.IsNullOrWhiteSpace(Message)) return "Ohne Text wäre die Antwort leer.";
            if (Days is < 1 or > 365) return "Die Frist muss zwischen 1 und 365 Tagen liegen.";
            return null;
        }
    }

    public VacationSettings Clone() => new()
    {
        Enabled = Enabled, Days = Days, Subject = Subject,
        Message = Message, Addresses = [.. Addresses],
    };
}

/// <summary>
/// Erzeugt aus Regeln ein Sieve-Skript und liest den erzeugten Teil wieder ein.
///
/// Der Assistent schreibt <b>nur zwischen seinen Markierungen</b>. Alles davor
/// und danach bleibt unangetastet: wer von Hand etwas ergänzt hat, soll es nicht
/// dadurch verlieren, dass er einmal den Assistenten öffnet. Beliebiges Sieve
/// zurückzulesen ist nicht möglich — der Versuch würde genau solche Verluste
/// erzeugen.
/// </summary>
public static class SieveGenerator
{
    public const string BeginMarker = "# --- Von N-MailClient verwaltet: nicht von Hand ändern ---";
    public const string EndMarker = "# --- Ende des verwalteten Bereichs ---";

    /// <summary>
    /// Das vollständige Skript bauen: der bestehende Fremdanteil bleibt, der
    /// verwaltete Block wird ersetzt.
    /// </summary>
    public static string Build(
        IEnumerable<SieveRule> rules, VacationSettings? vacation, string? existingScript = null)
    {
        var managed = BuildManagedBlock(rules, vacation);
        var (before, after) = SplitAroundBlock(existingScript ?? "");

        var result = new StringBuilder();

        if (before.Length > 0) result.Append(before.TrimEnd()).Append("\r\n\r\n");
        result.Append(managed);
        if (after.Length > 0) result.Append("\r\n").Append(after.TrimStart());

        return result.ToString();
    }

    private static string BuildManagedBlock(
        IEnumerable<SieveRule> rules, VacationSettings? vacation)
    {
        var list = rules.Where(r => r.Enabled).ToList();
        var body = new StringBuilder();

        foreach (var rule in list)
        {
            body.Append(Render(rule));
            body.Append("\r\n");
        }

        if (vacation is { Enabled: true }) body.Append(RenderVacation(vacation));

        var required = Requirements(list, vacation);
        var script = new StringBuilder();

        script.Append(BeginMarker).Append("\r\n");
        if (required.Count > 0)
        {
            script.Append("require [")
                  .Append(string.Join(", ", required.Select(Quote)))
                  .Append("];\r\n\r\n");
        }

        script.Append(body);
        script.Append(EndMarker).Append("\r\n");

        return script.ToString();
    }

    /// <summary>
    /// Die benötigten Erweiterungen aus dem tatsächlich Verwendeten ableiten.
    /// Pauschal alles anzufordern liesse das Skript auf Servern scheitern, die
    /// eine davon nicht können.
    /// </summary>
    public static IReadOnlyList<string> Requirements(
        IEnumerable<SieveRule> rules, VacationSettings? vacation = null)
    {
        var required = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var rule in rules)
        {
            foreach (var action in rule.Actions)
            {
                switch (action.Action)
                {
                    case RuleAction.FileInto: required.Add("fileinto"); break;
                    case RuleAction.Reject: required.Add("reject"); break;
                    case RuleAction.AddFlag:
                    case RuleAction.MarkRead: required.Add("imap4flags"); break;
                }
            }

            foreach (var condition in rule.Conditions)
            {
                if (condition.Field == RuleField.Body) required.Add("body");
                if (condition.Field == RuleField.AnyRecipient) required.Add("envelope");
            }
        }

        if (vacation is { Enabled: true }) required.Add("vacation");

        return [.. required];
    }

    // ---- Eine Regel --------------------------------------------------------

    private static string Render(SieveRule rule)
    {
        var script = new StringBuilder();

        script.Append("# ").Append(rule.Name).Append("\r\n");

        var tests = rule.Conditions.Select(RenderCondition).ToList();

        script.Append("if ").Append(tests.Count == 1
            ? tests[0]
            : $"{(rule.MatchAll ? "allof" : "anyof")} ({string.Join(", ", tests)})");

        script.Append("\r\n{\r\n");

        foreach (var action in rule.Actions)
            script.Append("    ").Append(RenderAction(action)).Append("\r\n");

        script.Append("}\r\n");
        return script.ToString();
    }

    private static string RenderCondition(RuleCondition condition)
    {
        // Grösse und Text sind eigene Prüfungen, kein Kopfzeilenvergleich.
        if (condition.Field == RuleField.Size)
        {
            var comparison = condition.Test == RuleTest.Under ? ":under" : ":over";
            return $"size {comparison} {condition.Value}K";
        }

        var match = condition.Test switch
        {
            RuleTest.Is => ":is",
            RuleTest.Matches => ":matches",
            _ => ":contains",
        };

        var test = condition.Field switch
        {
            RuleField.Body => $"body :text {match} {Quote(condition.Value)}",
            RuleField.AnyRecipient => $"envelope {match} \"to\" {Quote(condition.Value)}",
            _ => $"header {match} {Quote(condition.ResolvedHeader)} {Quote(condition.Value)}",
        };

        // Sieve kennt kein „enthält nicht"; das ist eine Verneinung des Tests.
        return condition.Test == RuleTest.NotContains ? $"not {test}" : test;
    }

    private static string RenderAction(RuleStep step) => step.Action switch
    {
        RuleAction.FileInto => $"fileinto {Quote(step.Value)};",
        RuleAction.Redirect => $"redirect {Quote(step.Value)};",
        RuleAction.Discard => "discard;",
        RuleAction.Reject => $"reject {Quote(step.Value)};",
        RuleAction.Keep => "keep;",
        RuleAction.AddFlag => $"addflag {Quote(step.Value)};",
        RuleAction.MarkRead => "addflag \"\\\\Seen\";",
        RuleAction.Stop => "stop;",
        _ => "keep;",
    };

    private static string RenderVacation(VacationSettings vacation)
    {
        var script = new StringBuilder();
        script.Append("# Abwesenheitsnotiz\r\n");
        script.Append("vacation\r\n");
        script.Append("    :days ").Append(vacation.Days).Append("\r\n");

        if (!string.IsNullOrWhiteSpace(vacation.Subject))
            script.Append("    :subject ").Append(Quote(vacation.Subject)).Append("\r\n");

        var addresses = vacation.Addresses
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => Quote(a.Trim()))
            .ToList();

        if (addresses.Count > 0)
            script.Append("    :addresses [").Append(string.Join(", ", addresses)).Append("]\r\n");

        script.Append("    ").Append(QuoteMultiline(vacation.Message)).Append(";\r\n");
        return script.ToString();
    }

    // ---- Zeichenketten -----------------------------------------------------

    /// <summary>
    /// Ein Sieve-String. Backslash und Anführungszeichen werden maskiert —
    /// ohne das genügte ein Anführungszeichen im Betreff, um das Skript zu
    /// zerlegen.
    /// </summary>
    public static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// Mehrzeiliger Text als <c>text:</c>-Block. Eine Zeile, die nur aus einem
    /// Punkt besteht, würde den Block vorzeitig beenden und wird deshalb
    /// entschärft (RFC 5228 §8.1).
    /// </summary>
    public static string QuoteMultiline(string value)
    {
        if (!value.Contains('\n') && !value.Contains('\r')) return Quote(value);

        var lines = value.Replace("\r\n", "\n").Split('\n')
                         .Select(l => l == "." ? ".." : l);

        return "text:\r\n" + string.Join("\r\n", lines) + "\r\n.";
    }

    // ---- Fremdanteil abtrennen ---------------------------------------------

    /// <summary>
    /// Das Skript in „vor dem Block" und „nach dem Block" zerlegen. Fehlen die
    /// Markierungen, gilt alles als fremd und bleibt erhalten.
    /// </summary>
    public static (string Before, string After) SplitAroundBlock(string script)
    {
        var begin = script.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0) return (script, "");

        var end = script.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        if (end < 0) return (script[..begin], "");

        return (script[..begin], script[(end + EndMarker.Length)..]);
    }

    /// <summary>Der verwaltete Block allein; leer, wenn keiner vorhanden ist.</summary>
    public static string ExtractBlock(string script)
    {
        var begin = script.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0) return "";

        var end = script.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        return end < 0 ? "" : script[begin..(end + EndMarker.Length)];
    }
}
