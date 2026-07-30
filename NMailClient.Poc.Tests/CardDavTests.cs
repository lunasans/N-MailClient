using NMailClient.Poc.Models;
using NMailClient.Poc.Services.Dav;
using Xunit;

namespace NMailClient.Poc.Tests;

public class VCardRoundTripTests
{
    [Fact]
    public void ContactSurvivesSerializationAndParsing()
    {
        var original = new Contact
        {
            Uid = "abc-123",
            FirstName = "Rene",
            LastName = "Neuhaus",
            Organization = "Beispiel GmbH",
            Emails = ["rene@example.org", "zweit@example.org"],
            Phones = ["+43 1 234567"],
            Birthday = new DateTime(1985, 6, 14),
        };

        var text = CardDavService.Serialize(original);
        var back = Assert.Single(CardDavService.Parse(text));

        Assert.Equal("Rene", back.FirstName);
        Assert.Equal("Neuhaus", back.LastName);
        Assert.Equal("Beispiel GmbH", back.Organization);
        Assert.Equal(2, back.Emails.Count);
        Assert.Contains("rene@example.org", back.Emails);
        Assert.Single(back.Phones);
        Assert.Equal(new DateTime(1985, 6, 14), back.Birthday);
    }

    [Fact]
    public void SerializedCardIsVersion4()
        => Assert.Contains("VERSION:4.0", CardDavService.Serialize(new Contact { Uid = "x" }));

    [Fact]
    public void ContactWithoutNameStillSerializes()
    {
        // Nur eine Adresse, sonst nichts – kommt beim Anlegen aus einer Mail vor.
        var contact = new Contact { Uid = "x", Emails = ["nur@example.org"] };

        var back = Assert.Single(CardDavService.Parse(CardDavService.Serialize(contact)));
        Assert.Contains("nur@example.org", back.Emails);
    }

    [Fact]
    public void ParsesSeveralCardsFromOneDocument()
    {
        var text = CardDavService.Serialize(new Contact { Uid = "a", DisplayName = "Anna" })
                 + CardDavService.Serialize(new Contact { Uid = "b", DisplayName = "Bernd" });

        var contacts = CardDavService.Parse(text);

        Assert.Equal(2, contacts.Count);
        Assert.Contains(contacts, c => c.Label == "Anna");
        Assert.Contains(contacts, c => c.Label == "Bernd");
    }

    [Fact]
    public void BrokenInputYieldsNoContactsInsteadOfThrowing()
        => Assert.Empty(CardDavService.Parse("kein vCard-Inhalt"));

    [Fact]
    public void EmailsAreDeduplicated()
    {
        var contact = new Contact { Uid = "x", Emails = ["a@b.c", "A@B.C"] };

        var back = Assert.Single(CardDavService.Parse(CardDavService.Serialize(contact)));
        Assert.Single(back.Emails);
    }
}

public class ContactLabelTests
{
    [Fact]
    public void PrefersDisplayName()
        => Assert.Equal("Anzeigename", new Contact
        {
            DisplayName = "Anzeigename", FirstName = "Vorname", Organization = "Firma",
        }.Label);

    [Fact]
    public void FallsBackThroughNameOrganizationAndAddress()
    {
        Assert.Equal("Rene Neuhaus",
            new Contact { FirstName = "Rene", LastName = "Neuhaus" }.Label);
        Assert.Equal("Beispiel GmbH", new Contact { Organization = "Beispiel GmbH" }.Label);
        Assert.Equal("a@b.c", new Contact { Emails = ["a@b.c"] }.Label);
        Assert.Equal("(ohne Namen)", new Contact().Label);
    }

    [Fact]
    public void RecipientFormatCombinesNameAndAddress()
        => Assert.Equal("Rene <rene@example.org>",
            new Contact { DisplayName = "Rene", Emails = ["rene@example.org"] }.AsRecipient);

    [Fact]
    public void RecipientIsPlainWhenNameEqualsAddress()
        => Assert.Equal("a@b.c", new Contact { Emails = ["a@b.c"] }.AsRecipient);

    [Fact]
    public void ContactWithoutAddressIsNoRecipient()
        => Assert.Equal("", new Contact { DisplayName = "Ohne Mail" }.AsRecipient);

    [Theory]
    [InlineData("neuhaus", true)]
    [InlineData("NEUHAUS", true)]
    [InlineData("example.org", true)]
    [InlineData("Beispiel", true)]
    [InlineData("234567", true)]
    [InlineData("kommt nicht vor", false)]
    public void SearchCoversAllVisibleFields(string term, bool expected)
    {
        var contact = new Contact
        {
            DisplayName = "Rene Neuhaus",
            Organization = "Beispiel GmbH",
            Emails = ["rene@example.org"],
            Phones = ["+43 1 234567"],
        };

        Assert.Equal(expected, contact.Matches(term));
    }

    [Fact]
    public void EmptySearchMatchesEverything()
        => Assert.True(new Contact().Matches(""));

    [Fact]
    public void NewContactIsRecognizedByMissingUrl()
    {
        Assert.True(new Contact().IsNew);
        Assert.False(new Contact { Url = "https://dav/ab/x.vcf" }.IsNew);
    }
}

public class DavUrlTests
{
    [Theory]
    [InlineData("https://dav.example.org/carddav/user/", "/carddav/user/book/",
                "https://dav.example.org/carddav/user/book/")]
    [InlineData("https://dav.example.org/a/b", "c.vcf", "https://dav.example.org/a/c.vcf")]
    [InlineData("https://dav.example.org/a/", "https://other.example.org/x",
                "https://other.example.org/x")]
    public void ResolvesHrefAgainstRequestUrl(string request, string href, string expected)
        => Assert.Equal(expected, DavDiscovery.Absolute(request, href));

    [Theory]
    [InlineData("https://dav.example.org/carddav/user/kontakte/", "kontakte")]
    [InlineData("https://dav.example.org/carddav/user/kontakte", "kontakte")]
    public void LastSegmentServesAsFallbackName(string url, string expected)
        => Assert.Equal(expected, DavDiscovery.LastSegment(url));
}
