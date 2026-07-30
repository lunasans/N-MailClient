using NMailClient.Poc.Services.Sieve;
using Xunit;

namespace NMailClient.Poc.Tests;

public class SieveGeneratorTests
{
    private static SieveRule Rule(string name = "Rechnungen") => new()
    {
        Name = name,
        Conditions = [new RuleCondition(RuleField.From, RuleTest.Contains, "buchhaltung@")],
        Actions = [new RuleStep(RuleAction.FileInto, "Rechnungen")],
    };

    [Fact]
    public void SimpleRuleBecomesReadableSieve()
    {
        var script = SieveGenerator.Build([Rule()], null);

        Assert.Contains("require [\"fileinto\"];", script);
        Assert.Contains("# Rechnungen", script);
        Assert.Contains("if header :contains \"from\" \"buchhaltung@\"", script);
        Assert.Contains("fileinto \"Rechnungen\";", script);
    }

    [Fact]
    public void RequirementsAreDerivedFromWhatIsActuallyUsed()
    {
        // Pauschal alles anzufordern liesse das Skript auf Servern scheitern,
        // die eine der Erweiterungen nicht können.
        Assert.Equal(["fileinto"], SieveGenerator.Requirements([Rule()]));

        var withFlags = Rule();
        withFlags.Actions.Add(new RuleStep(RuleAction.MarkRead));
        Assert.Equal(["fileinto", "imap4flags"], SieveGenerator.Requirements([withFlags]));

        var plain = new SieveRule { Actions = [new RuleStep(RuleAction.Discard)] };
        Assert.Empty(SieveGenerator.Requirements([plain]));
    }

    [Fact]
    public void BodyAndEnvelopeBringTheirOwnRequirements()
    {
        var rule = new SieveRule
        {
            Conditions =
            [
                new RuleCondition(RuleField.Body, RuleTest.Contains, "Angebot"),
                new RuleCondition(RuleField.AnyRecipient, RuleTest.Is, "info@example.org"),
            ],
            Actions = [new RuleStep(RuleAction.Keep)],
        };

        Assert.Equal(["body", "envelope"], SieveGenerator.Requirements([rule]));
    }

    [Fact]
    public void SeveralConditionsUseAllofOrAnyof()
    {
        var rule = Rule();
        rule.Conditions.Add(new RuleCondition(RuleField.Subject, RuleTest.Contains, "Mahnung"));

        rule.MatchAll = true;
        Assert.Contains("allof (", SieveGenerator.Build([rule], null));

        rule.MatchAll = false;
        Assert.Contains("anyof (", SieveGenerator.Build([rule], null));
    }

    [Fact]
    public void SingleConditionNeedsNoGrouping()
    {
        var script = SieveGenerator.Build([Rule()], null);

        Assert.DoesNotContain("allof", script);
        Assert.DoesNotContain("anyof", script);
    }

    [Fact]
    public void NotContainsBecomesANegatedTest()
    {
        var rule = new SieveRule
        {
            Name = "Nicht intern",
            Conditions = [new RuleCondition(RuleField.From, RuleTest.NotContains, "@example.org")],
            Actions = [new RuleStep(RuleAction.FileInto, "Extern")],
        };

        Assert.Contains("if not header :contains \"from\" \"@example.org\"",
                        SieveGenerator.Build([rule], null));
    }

    [Fact]
    public void SizeIsItsOwnTestNotAHeaderComparison()
    {
        var rule = new SieveRule
        {
            Name = "Grosse Post",
            Conditions = [new RuleCondition(RuleField.Size, RuleTest.Over, "5000")],
            Actions = [new RuleStep(RuleAction.FileInto, "Gross")],
        };

        Assert.Contains("size :over 5000K", SieveGenerator.Build([rule], null));
    }

    [Fact]
    public void MarkReadUsesTheSystemFlag()
    {
        var rule = Rule();
        rule.Actions.Add(new RuleStep(RuleAction.MarkRead));

        Assert.Contains("addflag \"\\\\Seen\";", SieveGenerator.Build([rule], null));
    }

    [Fact]
    public void QuotesInValuesAreEscaped()
    {
        // Ohne Maskierung genügte ein Anführungszeichen im Betreff, um das
        // Skript zu zerlegen – und der Server lehnte alles ab.
        var rule = new SieveRule
        {
            Name = "Zitat",
            Conditions = [new RuleCondition(RuleField.Subject, RuleTest.Contains, "sagte \"nein\"")],
            Actions = [new RuleStep(RuleAction.FileInto, "Pfad\\mit\\Backslash")],
        };

        var script = SieveGenerator.Build([rule], null);

        Assert.Contains("\"sagte \\\"nein\\\"\"", script);
        Assert.Contains("\"Pfad\\\\mit\\\\Backslash\"", script);
    }

    [Fact]
    public void DisabledRulesAreLeftOut()
    {
        var off = Rule("Abgeschaltet");
        off.Enabled = false;

        var script = SieveGenerator.Build([Rule(), off], null);

        Assert.Contains("# Rechnungen", script);
        Assert.DoesNotContain("Abgeschaltet", script);
    }

    // ---- Fremdanteil -------------------------------------------------------

    [Fact]
    public void HandWrittenContentIsPreserved()
    {
        // Der wichtigste Punkt des ganzen Entwurfs: wer von Hand etwas ergänzt
        // hat, darf es nicht dadurch verlieren, dass er den Assistenten öffnet.
        const string existing = """
            # von Hand geschrieben
            require ["imap4flags"];
            if header :contains "x-sonderfall" "ja" { setflag "\\Flagged"; }
            """;

        var script = SieveGenerator.Build([Rule()], null, existing);

        Assert.Contains("von Hand geschrieben", script);
        Assert.Contains("x-sonderfall", script);
        Assert.Contains("# Rechnungen", script);
    }

    [Fact]
    public void RepeatedBuildsDoNotPileUpBlocks()
    {
        var first = SieveGenerator.Build([Rule()], null);
        var second = SieveGenerator.Build([Rule("Anders")], null, first);
        var third = SieveGenerator.Build([Rule("Nochmal")], null, second);

        Assert.Equal(1, CountOccurrences(third, SieveGenerator.BeginMarker));
        Assert.Equal(1, CountOccurrences(third, SieveGenerator.EndMarker));
        Assert.Contains("# Nochmal", third);
        Assert.DoesNotContain("# Anders", third);
    }

    [Fact]
    public void ForeignContentAfterTheBlockAlsoSurvives()
    {
        var script = SieveGenerator.Build([Rule()], null);
        var withTail = script + "\r\n# Nachwort\r\nkeep;\r\n";

        var rebuilt = SieveGenerator.Build([Rule("Neu")], null, withTail);

        Assert.Contains("# Nachwort", rebuilt);
        Assert.Contains("# Neu", rebuilt);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}

public class VacationTests
{
    private static VacationSettings Sample() => new()
    {
        Enabled = true,
        Days = 14,
        Subject = "Bin weg",
        Message = "Ich bin bis zum 1. August nicht erreichbar.",
        Addresses = ["rene@example.org", "info@example.org"],
    };

    [Fact]
    public void VacationIsRendered()
    {
        var script = SieveGenerator.Build([], Sample());

        Assert.Contains("require [\"vacation\"];", script);
        Assert.Contains(":days 14", script);
        Assert.Contains(":subject \"Bin weg\"", script);
        Assert.Contains(":addresses [\"rene@example.org\", \"info@example.org\"]", script);
        Assert.Contains("nicht erreichbar", script);
    }

    [Fact]
    public void DisabledVacationIsLeftOut()
    {
        var vacation = Sample();
        vacation.Enabled = false;

        var script = SieveGenerator.Build([], vacation);

        Assert.DoesNotContain("vacation", script);
    }

    [Fact]
    public void MultilineMessageBecomesATextBlock()
    {
        var vacation = Sample();
        vacation.Message = "Erste Zeile\r\nZweite Zeile";

        var script = SieveGenerator.Build([], vacation);

        Assert.Contains("text:", script);
        Assert.Contains("Erste Zeile", script);
        Assert.Contains("Zweite Zeile", script);
    }

    [Fact]
    public void ALoneDotIsNeutralised()
    {
        // Eine Zeile aus einem einzelnen Punkt beendet den text:-Block. Ohne
        // Entschärfung landete der Rest der Nachricht als Sieve-Befehl im Skript.
        var vacation = Sample();
        vacation.Message = "Vor dem Punkt\r\n.\r\nNach dem Punkt";

        var script = SieveGenerator.Build([], vacation);

        Assert.Contains("\r\n..\r\n", script);
        Assert.Contains("Nach dem Punkt", script);
    }

    [Fact]
    public void ProblemsAreNamed()
    {
        Assert.Null(Sample().Problem);

        var empty = Sample();
        empty.Message = "";
        Assert.Contains("Text", empty.Problem);

        var wrongDays = Sample();
        wrongDays.Days = 0;
        Assert.Contains("Frist", wrongDays.Problem);

        var off = Sample();
        off.Enabled = false;
        off.Message = "";
        Assert.Null(off.Problem);      // abgeschaltet ist nie ein Problem
    }
}

public class SieveRoundTripTests
{
    /// <summary>
    /// Der eigentliche Prüfstein: erzeugen, zurücklesen, wieder erzeugen — und
    /// dasselbe herausbekommen.
    /// </summary>
    private static void AssertRoundTrip(IReadOnlyList<SieveRule> rules, VacationSettings? vacation = null)
    {
        var script = SieveGenerator.Build(rules, vacation);
        var parsed = SieveRuleParser.Parse(script);
        var again = SieveGenerator.Build(parsed.Rules, parsed.Vacation);

        Assert.Equal(script, again);
    }

    [Fact]
    public void SimpleRuleSurvives()
    {
        var rule = new SieveRule
        {
            Name = "Rechnungen",
            Conditions = [new RuleCondition(RuleField.From, RuleTest.Contains, "buchhaltung@")],
            Actions = [new RuleStep(RuleAction.FileInto, "Rechnungen")],
        };

        AssertRoundTrip([rule]);
    }

    [Fact]
    public void SeveralConditionsAndActionsSurvive()
    {
        var rule = new SieveRule
        {
            Name = "Newsletter",
            MatchAll = false,
            Conditions =
            [
                new RuleCondition(RuleField.Subject, RuleTest.Contains, "Newsletter"),
                new RuleCondition(RuleField.From, RuleTest.Is, "news@example.org"),
            ],
            Actions =
            [
                new RuleStep(RuleAction.FileInto, "Newsletter"),
                new RuleStep(RuleAction.MarkRead),
                new RuleStep(RuleAction.Stop),
            ],
        };

        AssertRoundTrip([rule]);
    }

    [Fact]
    public void EscapedValuesSurvive()
    {
        var rule = new SieveRule
        {
            Name = "Zitat",
            Conditions = [new RuleCondition(RuleField.Subject, RuleTest.Contains, "sagte \"nein\"")],
            Actions = [new RuleStep(RuleAction.FileInto, "Pfad\\Unter")],
        };

        AssertRoundTrip([rule]);

        var parsed = SieveRuleParser.Parse(SieveGenerator.Build([rule], null));
        Assert.Equal("sagte \"nein\"", parsed.Rules[0].Conditions[0].Value);
        Assert.Equal("Pfad\\Unter", parsed.Rules[0].Actions[0].Value);
    }

    [Fact]
    public void NegationSurvives()
    {
        var rule = new SieveRule
        {
            Name = "Extern",
            Conditions = [new RuleCondition(RuleField.From, RuleTest.NotContains, "@example.org")],
            Actions = [new RuleStep(RuleAction.FileInto, "Extern")],
        };

        AssertRoundTrip([rule]);

        var parsed = SieveRuleParser.Parse(SieveGenerator.Build([rule], null));
        Assert.Equal(RuleTest.NotContains, parsed.Rules[0].Conditions[0].Test);
    }

    [Fact]
    public void CustomHeaderSurvives()
    {
        var rule = new SieveRule
        {
            Name = "Sonderfall",
            Conditions =
                [new RuleCondition(RuleField.Header, RuleTest.Is, "ja", "X-Sonderfall")],
            Actions = [new RuleStep(RuleAction.Keep)],
        };

        var parsed = SieveRuleParser.Parse(SieveGenerator.Build([rule], null));
        var back = parsed.Rules[0].Conditions[0];

        Assert.Equal(RuleField.Header, back.Field);
        Assert.Equal("X-Sonderfall", back.HeaderName, ignoreCase: true);
        Assert.Equal("ja", back.Value);
    }

    [Fact]
    public void SizeSurvives()
    {
        var rule = new SieveRule
        {
            Name = "Gross",
            Conditions = [new RuleCondition(RuleField.Size, RuleTest.Over, "5000")],
            Actions = [new RuleStep(RuleAction.FileInto, "Gross")],
        };

        AssertRoundTrip([rule]);
    }

    [Fact]
    public void VacationSurvives()
    {
        var vacation = new VacationSettings
        {
            Enabled = true, Days = 21, Subject = "Urlaub",
            Message = "Erste Zeile\r\nZweite Zeile",
            Addresses = ["rene@example.org"],
        };

        var parsed = SieveRuleParser.Parse(SieveGenerator.Build([], vacation));

        Assert.True(parsed.Vacation.Enabled);
        Assert.Equal(21, parsed.Vacation.Days);
        Assert.Equal("Urlaub", parsed.Vacation.Subject);
        Assert.Equal(["rene@example.org"], parsed.Vacation.Addresses);
        Assert.Contains("Zweite Zeile", parsed.Vacation.Message);
    }

    [Fact]
    public void SeveralRulesKeepTheirOrder()
    {
        var rules = Enumerable.Range(1, 4).Select(i => new SieveRule
        {
            Name = $"Regel {i}",
            Conditions = [new RuleCondition(RuleField.Subject, RuleTest.Contains, $"Wort{i}")],
            Actions = [new RuleStep(RuleAction.FileInto, $"Ordner{i}")],
        }).ToList();

        var parsed = SieveRuleParser.Parse(SieveGenerator.Build(rules, null));

        Assert.Equal(["Regel 1", "Regel 2", "Regel 3", "Regel 4"],
                     parsed.Rules.Select(r => r.Name));
    }

    [Fact]
    public void ForeignContentIsReportedAndNotParsedAsRules()
    {
        const string foreign = "# eigene Regel\r\nif true { keep; }\r\n";
        var script = SieveGenerator.Build([], null, foreign);

        var parsed = SieveRuleParser.Parse(script);

        Assert.True(parsed.HasForeignContent);
        Assert.Empty(parsed.Rules);
    }

    [Fact]
    public void ScriptWithoutOurBlockYieldsNothingButIsNotLost()
    {
        var parsed = SieveRuleParser.Parse("require [\"fileinto\"];\r\nkeep;\r\n");

        Assert.Empty(parsed.Rules);
        Assert.False(parsed.Vacation.Enabled);
        Assert.True(parsed.HasForeignContent);
    }

    [Fact]
    public void EmptyScriptIsHandled()
    {
        var parsed = SieveRuleParser.Parse("");

        Assert.Empty(parsed.Rules);
        Assert.False(parsed.HasForeignContent);
    }
}

public class SieveRuleValidationTests
{
    [Fact]
    public void RuleWithoutConditionIsRejected()
    {
        var rule = new SieveRule { Actions = [new RuleStep(RuleAction.Keep)] };

        Assert.Contains("jede Nachricht", rule.Problem);
    }

    [Fact]
    public void RuleWithoutActionIsRejected()
    {
        var rule = new SieveRule
        {
            Conditions = [new RuleCondition(RuleField.From, RuleTest.Contains, "x@y")],
        };

        Assert.Contains("tut nichts", rule.Problem);
    }

    [Fact]
    public void EmptyValuesAreRejected()
    {
        var rule = new SieveRule
        {
            Conditions = [new RuleCondition(RuleField.From, RuleTest.Contains, "")],
            Actions = [new RuleStep(RuleAction.Keep)],
        };

        Assert.Contains("keinen Wert", rule.Problem);
    }

    [Fact]
    public void SizeMustBeANumber()
    {
        var rule = new SieveRule
        {
            Conditions = [new RuleCondition(RuleField.Size, RuleTest.Over, "gross")],
            Actions = [new RuleStep(RuleAction.Keep)],
        };

        Assert.Contains("Zahl", rule.Problem);
    }

    [Fact]
    public void CompleteRuleHasNoProblem()
    {
        var rule = new SieveRule
        {
            Name = "Vollständig",
            Conditions = [new RuleCondition(RuleField.From, RuleTest.Contains, "x@y")],
            Actions = [new RuleStep(RuleAction.FileInto, "Ordner")],
        };

        Assert.Null(rule.Problem);
    }

    [Fact]
    public void CloneIsIndependent()
    {
        var rule = new SieveRule
        {
            Name = "Original",
            Conditions = [new RuleCondition(RuleField.From, RuleTest.Contains, "x@y")],
            Actions = [new RuleStep(RuleAction.Keep)],
        };

        var copy = rule.Clone();
        copy.Name = "Kopie";
        copy.Conditions.Clear();

        Assert.Equal("Original", rule.Name);
        Assert.Single(rule.Conditions);
    }
}
