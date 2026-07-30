namespace NMailClient.Services.Sieve;

/// <summary>Worauf sich eine Bedingung bezieht.</summary>
public enum RuleField { From, To, Cc, Subject, AnyRecipient, Header, Size, Body }

/// <summary>Wie verglichen wird.</summary>
public enum RuleTest { Contains, Is, Matches, NotContains, Over, Under }

/// <summary>Was geschehen soll.</summary>
public enum RuleAction { FileInto, Redirect, Discard, Reject, Keep, AddFlag, MarkRead, Stop }

/// <summary>Eine einzelne Bedingung.</summary>
public sealed record RuleCondition(
    RuleField Field, RuleTest Test, string Value, string? HeaderName = null)
{
    /// <summary>Der Kopfzeilenname für Sieve; bei <see cref="RuleField.Header"/> frei.</summary>
    public string ResolvedHeader => Field switch
    {
        RuleField.From => "from",
        RuleField.To => "to",
        RuleField.Cc => "cc",
        RuleField.Subject => "subject",
        RuleField.Header => string.IsNullOrWhiteSpace(HeaderName) ? "x-unbekannt" : HeaderName,
        _ => "",
    };

    public string Display => BuildDisplay();

    private string BuildDisplay()
    {
        var field = Field switch
        {
            RuleField.From => "Absender",
            RuleField.To => "Empfänger",
            RuleField.Cc => "Kopie",
            RuleField.Subject => "Betreff",
            RuleField.AnyRecipient => "Beliebiger Empfänger",
            RuleField.Header => HeaderName ?? "Kopfzeile",
            RuleField.Size => "Grösse",
            RuleField.Body => "Nachrichtentext",
            _ => Field.ToString(),
        };

        var test = Test switch
        {
            RuleTest.Contains => "enthält",
            RuleTest.Is => "ist genau",
            RuleTest.Matches => "passt auf",
            RuleTest.NotContains => "enthält nicht",
            RuleTest.Over => "grösser als",
            RuleTest.Under => "kleiner als",
            _ => Test.ToString(),
        };

        return $"{field} {test} „{Value}“";
    }
}

/// <summary>Eine auszuführende Aktion.</summary>
public sealed record RuleStep(RuleAction Action, string Value = "")
{
    public string Display => Action switch
    {
        RuleAction.FileInto => $"Ablegen in „{Value}“",
        RuleAction.Redirect => $"Weiterleiten an {Value}",
        RuleAction.Discard => "Verwerfen (ohne Hinweis)",
        RuleAction.Reject => $"Zurückweisen: „{Value}“",
        RuleAction.Keep => "Im Posteingang behalten",
        RuleAction.AddFlag => $"Etikett „{Value}“ setzen",
        RuleAction.MarkRead => "Als gelesen markieren",
        RuleAction.Stop => "Weitere Regeln nicht mehr prüfen",
        _ => Action.ToString(),
    };
}

/// <summary>
/// Eine Filterregel des Assistenten.
///
/// Bewusst nicht der volle Sprachumfang von Sieve: der Assistent deckt ab, was
/// im Alltag gebraucht wird. Wer mehr will, schreibt das Skript von Hand — und
/// genau das bleibt möglich, weil der Assistent nur seinen eigenen Block anfasst.
/// </summary>
public sealed class SieveRule
{
    public string Name { get; set; } = "Neue Regel";

    /// <summary>Wahr = alle Bedingungen müssen zutreffen, falsch = eine genügt.</summary>
    public bool MatchAll { get; set; } = true;

    public bool Enabled { get; set; } = true;

    public List<RuleCondition> Conditions { get; set; } = [];
    public List<RuleStep> Actions { get; set; } = [];

    public string Display => Enabled ? Name : $"{Name}  (aus)";

    /// <summary>
    /// Fehlt etwas Wesentliches? Eine Regel ohne Bedingung träfe auf jede
    /// Nachricht zu, eine ohne Aktion täte nichts — beides ist mit Sicherheit
    /// nicht gemeint.
    /// </summary>
    public string? Problem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name)) return "Die Regel braucht einen Namen.";
            if (Conditions.Count == 0) return "Ohne Bedingung träfe die Regel auf jede Nachricht zu.";
            if (Actions.Count == 0) return "Die Regel tut nichts – es fehlt eine Aktion.";

            foreach (var c in Conditions)
            {
                if (string.IsNullOrWhiteSpace(c.Value))
                    return $"Die Bedingung „{c.Display}“ hat keinen Wert.";

                if (c.Field is RuleField.Size && !long.TryParse(c.Value, out _))
                    return "Die Grösse muss eine Zahl in Kilobyte sein.";
            }

            foreach (var a in Actions)
            {
                if (a.Action is RuleAction.FileInto or RuleAction.Redirect
                             or RuleAction.Reject or RuleAction.AddFlag
                    && string.IsNullOrWhiteSpace(a.Value))
                    return $"Der Aktion „{a.Display}“ fehlt ein Wert.";
            }

            return null;
        }
    }

    public SieveRule Clone() => new()
    {
        Name = Name,
        MatchAll = MatchAll,
        Enabled = Enabled,
        Conditions = [.. Conditions],
        Actions = [.. Actions],
    };
}
