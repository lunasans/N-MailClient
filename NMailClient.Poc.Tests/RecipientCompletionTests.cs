using NMailClient.Poc.Models;
using NMailClient.Poc.Services;
using Xunit;

namespace NMailClient.Poc.Tests;

public class CurrentTokenTests
{
    [Fact]
    public void EmptyFieldYieldsEmptyToken()
    {
        var (start, length, text) = RecipientCompletion.CurrentToken("", 0);

        Assert.Equal(0, start);
        Assert.Equal(0, length);
        Assert.Equal("", text);
    }

    [Fact]
    public void SingleWordIsTheWholeField()
    {
        var (start, _, text) = RecipientCompletion.CurrentToken("rene", 4);

        Assert.Equal(0, start);
        Assert.Equal("rene", text);
    }

    [Fact]
    public void TokenAfterCommaStartsAfterTheSeparator()
    {
        const string input = "a@b.c, rene";
        var (start, _, text) = RecipientCompletion.CurrentToken(input, input.Length);

        Assert.Equal(6, start);          // direkt hinter dem Komma
        Assert.Equal("rene", text);      // ohne führendes Leerzeichen
    }

    [Fact]
    public void SemicolonWorksAsSeparatorToo()
    {
        const string input = "a@b.c; ren";
        var (_, _, text) = RecipientCompletion.CurrentToken(input, input.Length);
        Assert.Equal("ren", text);
    }

    [Fact]
    public void CaretInTheMiddleFindsTheSurroundingToken()
    {
        // Einfügemarke steht im zweiten Eintrag, nicht am Ende.
        const string input = "erster@x.de, zweiter@y.de, dritter@z.de";
        var caret = input.IndexOf("zweiter", StringComparison.Ordinal) + 3;

        var (_, _, text) = RecipientCompletion.CurrentToken(input, caret);
        Assert.Equal("zweiter@y.de", text);
    }

    [Fact]
    public void CaretBeyondTheTextIsClamped()
    {
        var (_, _, text) = RecipientCompletion.CurrentToken("abc", 999);
        Assert.Equal("abc", text);
    }
}

public class ReplaceTokenTests
{
    [Fact]
    public void ReplacesTheOnlyEntryAndAppendsSeparator()
    {
        var (text, caret) = RecipientCompletion.Replace("ren", 3, "Rene <rene@example.org>");

        Assert.Equal("Rene <rene@example.org>, ", text);
        Assert.Equal(text.Length, caret);
    }

    [Fact]
    public void KeepsEarlierRecipientsIntact()
    {
        const string input = "erster@x.de, ren";
        var (text, _) = RecipientCompletion.Replace(input, input.Length, "Rene <rene@example.org>");

        Assert.StartsWith("erster@x.de, ", text);
        Assert.Contains("rene@example.org", text);
    }

    [Fact]
    public void KeepsLaterRecipientsIntact()
    {
        // Einfügemarke im ersten Eintrag – der zweite darf nicht verloren gehen.
        const string input = "ren, dritter@z.de";
        var (text, _) = RecipientCompletion.Replace(input, 3, "Rene <rene@example.org>");

        Assert.Contains("dritter@z.de", text);
        Assert.DoesNotContain("ren,", text);
    }

    [Fact]
    public void DoesNotAddASecondSeparator()
    {
        const string input = "ren, dritter@z.de";
        var (text, _) = RecipientCompletion.Replace(input, 3, "rene@example.org");

        Assert.DoesNotContain(",,", text.Replace(" ", ""));
    }

    [Fact]
    public void CaretLandsAfterTheInsertedText()
    {
        const string input = "ren, dritter@z.de";
        var (text, caret) = RecipientCompletion.Replace(input, 3, "rene@example.org");

        // Alles vor der Marke ist der eingesetzte Empfänger.
        Assert.Equal("rene@example.org", text[..caret].TrimEnd());
    }
}

public class SuggestTests
{
    private static readonly Contact[] Contacts =
    [
        new() { Uid = "1", DisplayName = "Rene Neuhaus", Emails = ["rene@example.org"] },
        new() { Uid = "2", DisplayName = "Renate Berger", Emails = ["renate@example.org"] },
        new() { Uid = "3", DisplayName = "Ohne Adresse" },
        new() { Uid = "4", DisplayName = "Anna", Emails = ["anna@rendite.de"] },
    ];

    [Fact]
    public void ShortInputSuggestsNothing()
    {
        // Bei einem Zeichen wäre praktisch jeder Kontakt ein Treffer.
        Assert.Empty(RecipientCompletion.Suggest(Contacts, "r"));
        Assert.Empty(RecipientCompletion.Suggest(Contacts, ""));
    }

    [Fact]
    public void ContactsWithoutAddressAreSkipped()
    {
        var result = RecipientCompletion.Suggest(Contacts, "Ohne");
        Assert.Empty(result);
    }

    [Fact]
    public void MatchesNameAndAddress()
    {
        Assert.Contains(RecipientCompletion.Suggest(Contacts, "Neuhaus"),
            c => c.PrimaryEmail == "rene@example.org");

        Assert.Contains(RecipientCompletion.Suggest(Contacts, "renate@"),
            c => c.PrimaryEmail == "renate@example.org");
    }

    [Fact]
    public void PrefixMatchesComeFirst()
    {
        // "ren" trifft alle drei – die mit passendem Wortanfang gehören nach oben.
        var result = RecipientCompletion.Suggest(Contacts, "ren");

        Assert.Equal(3, result.Count);
        Assert.DoesNotContain(result.Take(2), c => c.Label == "Anna");
    }

    [Fact]
    public void ResultsAreLimited()
    {
        var many = Enumerable.Range(0, 50)
            .Select(i => new Contact { Uid = $"c{i}", DisplayName = $"Test {i}", Emails = [$"t{i}@x.de"] })
            .ToList();

        Assert.Equal(8, RecipientCompletion.Suggest(many, "Test").Count);
        Assert.Equal(3, RecipientCompletion.Suggest(many, "Test", max: 3).Count);
    }
}
