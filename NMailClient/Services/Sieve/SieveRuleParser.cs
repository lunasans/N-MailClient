using System.Text;
using System.Text.RegularExpressions;

namespace NMailClient.Services.Sieve;

/// <summary>Was beim Rücklesen herauskam.</summary>
public sealed record ParsedScript(
    IReadOnlyList<SieveRule> Rules, VacationSettings Vacation, bool HasForeignContent);

/// <summary>
/// Liest den vom Assistenten erzeugten Block zurück.
///
/// Ausdrücklich <b>kein</b> allgemeiner Sieve-Parser: verstanden wird genau das,
/// was <see cref="SieveGenerator"/> schreibt. Alles andere bleibt als Fremdtext
/// stehen und wird nie verändert — ein Assistent, der handgeschriebene Regeln
/// halb versteht und dann neu schreibt, richtet mehr Schaden an als er nützt.
/// </summary>
public static partial class SieveRuleParser
{
    public static ParsedScript Parse(string script)
    {
        var (before, after) = SieveGenerator.SplitAroundBlock(script);
        var foreign = (before + after).Trim().Length > 0;

        var block = SieveGenerator.ExtractBlock(script);
        if (block.Length == 0) return new ParsedScript([], new VacationSettings(), foreign);

        var rules = new List<SieveRule>();
        var vacation = new VacationSettings();

        var lines = block.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            // Der Kommentar über einem "if" trägt den Namen der Regel.
            if (!line.StartsWith("# ") || line.StartsWith(SieveGenerator.BeginMarker)
                                       || line.StartsWith(SieveGenerator.EndMarker))
                continue;

            var name = line[2..].Trim();

            if (name == "Abwesenheitsnotiz")
            {
                vacation = ParseVacation(lines, i);
                continue;
            }

            if (ParseRule(name, lines, i) is { } rule) rules.Add(rule);
        }

        return new ParsedScript(rules, vacation, foreign);
    }

    // ---- Regeln ------------------------------------------------------------

    private static SieveRule? ParseRule(string name, string[] lines, int start)
    {
        var i = start + 1;
        while (i < lines.Length && lines[i].Trim().Length == 0) i++;
        if (i >= lines.Length || !lines[i].TrimStart().StartsWith("if ")) return null;

        var rule = new SieveRule { Name = name, Enabled = true };
        var condition = lines[i].Trim()[3..].Trim();

        // Mehrzeilige Bedingung bis zur öffnenden Klammer einsammeln.
        while (!condition.EndsWith('{') && i + 1 < lines.Length && lines[i + 1].Trim() != "{")
        {
            i++;
            condition += " " + lines[i].Trim();
        }
        condition = condition.TrimEnd('{').Trim();

        rule.MatchAll = !condition.StartsWith("anyof", StringComparison.Ordinal);
        rule.Conditions = ParseConditions(condition);

        // Aktionen bis zur schliessenden Klammer.
        while (i < lines.Length && lines[i].Trim() != "{") i++;
        i++;

        while (i < lines.Length && lines[i].Trim() != "}")
        {
            if (ParseAction(lines[i].Trim()) is { } action) rule.Actions.Add(action);
            i++;
        }

        return rule.Conditions.Count == 0 && rule.Actions.Count == 0 ? null : rule;
    }

    private static List<RuleCondition> ParseConditions(string text)
    {
        var body = text;

        // allof(...) beziehungsweise anyof(...) auspacken.
        var group = GroupPattern().Match(body);
        if (group.Success) body = group.Groups[1].Value;

        var result = new List<RuleCondition>();

        foreach (Match match in TestPattern().Matches(body))
        {
            var negated = match.Groups["not"].Success;
            var kind = match.Groups["kind"].Value;
            var comparator = match.Groups["cmp"].Value;
            var first = Unquote(match.Groups["a"].Value);
            var second = match.Groups["b"].Success ? Unquote(match.Groups["b"].Value) : null;

            var test = comparator switch
            {
                ":is" => RuleTest.Is,
                ":matches" => RuleTest.Matches,
                _ => negated ? RuleTest.NotContains : RuleTest.Contains,
            };

            switch (kind)
            {
                case "header":
                    result.Add(new RuleCondition(FieldFor(first), test, second ?? "",
                        FieldFor(first) == RuleField.Header ? first : null));
                    break;

                case "body":
                    result.Add(new RuleCondition(RuleField.Body, test, first));
                    break;

                case "envelope":
                    result.Add(new RuleCondition(RuleField.AnyRecipient, test, second ?? ""));
                    break;
            }
        }

        foreach (Match match in SizePattern().Matches(body))
        {
            var over = match.Groups["cmp"].Value == ":over";
            result.Add(new RuleCondition(
                RuleField.Size, over ? RuleTest.Over : RuleTest.Under, match.Groups["n"].Value));
        }

        return result;
    }

    private static RuleField FieldFor(string header) => header.ToLowerInvariant() switch
    {
        "from" => RuleField.From,
        "to" => RuleField.To,
        "cc" => RuleField.Cc,
        "subject" => RuleField.Subject,
        _ => RuleField.Header,
    };

    private static RuleStep? ParseAction(string line)
    {
        var text = line.TrimEnd(';').Trim();

        if (text.StartsWith("fileinto ")) return new RuleStep(RuleAction.FileInto, Unquote(text[9..]));
        if (text.StartsWith("redirect ")) return new RuleStep(RuleAction.Redirect, Unquote(text[9..]));
        if (text.StartsWith("reject ")) return new RuleStep(RuleAction.Reject, Unquote(text[7..]));

        if (text.StartsWith("addflag "))
        {
            var flag = Unquote(text[8..]);

            // "\\Seen" ist die Markierung „gelesen", kein gewöhnliches Etikett.
            return flag is "\\Seen" or "\\\\Seen"
                ? new RuleStep(RuleAction.MarkRead)
                : new RuleStep(RuleAction.AddFlag, flag);
        }

        return text switch
        {
            "discard" => new RuleStep(RuleAction.Discard),
            "keep" => new RuleStep(RuleAction.Keep),
            "stop" => new RuleStep(RuleAction.Stop),
            _ => null,
        };
    }

    // ---- Abwesenheitsnotiz -------------------------------------------------

    private static VacationSettings ParseVacation(string[] lines, int start)
    {
        var vacation = new VacationSettings { Enabled = true };
        var i = start + 1;

        while (i < lines.Length && !lines[i].TrimStart().StartsWith("vacation")) i++;

        var message = new StringBuilder();
        var inText = false;

        for (i++; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (inText)
            {
                if (trimmed == ".") break;
                if (message.Length > 0) message.Append("\r\n");
                message.Append(trimmed == ".." ? "." : line.TrimEnd());
                continue;
            }

            if (trimmed.StartsWith(":days "))
            {
                if (int.TryParse(trimmed[6..].Trim(), out var days)) vacation.Days = days;
                continue;
            }

            if (trimmed.StartsWith(":subject "))
            {
                vacation.Subject = Unquote(trimmed[9..]);
                continue;
            }

            if (trimmed.StartsWith(":addresses "))
            {
                vacation.Addresses = QuotedPattern()
                    .Matches(trimmed[11..])
                    .Select(m => Unquote(m.Value))
                    .ToList();
                continue;
            }

            if (trimmed.StartsWith("text:")) { inText = true; continue; }

            // Einzeilige Nachricht in Anführungszeichen.
            if (trimmed.StartsWith('"'))
            {
                vacation.Message = Unquote(trimmed.TrimEnd(';'));
                break;
            }

            if (trimmed.Length == 0) continue;
            break;
        }

        if (message.Length > 0) vacation.Message = message.ToString();
        return vacation;
    }

    // ---- Hilfen ------------------------------------------------------------

    private static string Unquote(string value)
    {
        var text = value.Trim().TrimEnd(';').Trim();
        if (text.Length < 2 || !text.StartsWith('"') || !text.EndsWith('"')) return text;

        var inner = text[1..^1];
        var result = new StringBuilder(inner.Length);

        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length) i++;
            result.Append(inner[i]);
        }

        return result.ToString();
    }

    [GeneratedRegex(@"^(?:allof|anyof)\s*\((.*)\)\s*$", RegexOptions.Singleline)]
    private static partial Regex GroupPattern();

    [GeneratedRegex(
        @"(?<not>not\s+)?(?<kind>header|body|envelope)\s+(?::text\s+)?(?<cmp>:is|:matches|:contains)\s+(?<a>""(?:[^""\\]|\\.)*"")(?:\s+(?<b>""(?:[^""\\]|\\.)*""))?")]
    private static partial Regex TestPattern();

    [GeneratedRegex(@"size\s+(?<cmp>:over|:under)\s+(?<n>\d+)K?")]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"""(?:[^""\\]|\\.)*""")]
    private static partial Regex QuotedPattern();
}
