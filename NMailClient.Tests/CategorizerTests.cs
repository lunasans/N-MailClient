using NMailClient.Models;
using NMailClient.Services;
using Xunit;

namespace NMailClient.Tests;

public class MailCategorizerTests
{
    private static MailSummary M(
        string from = "person@example.org", string? listUnsubscribe = null,
        string? precedence = null, string? autoSubmitted = null)
        => new()
        {
            FromAddress = from,
            ListUnsubscribe = listUnsubscribe,
            Precedence = precedence,
            AutoSubmitted = autoSubmitted,
        };

    [Fact]
    public void PersonalMailStaysGeneral()
        => Assert.Equal(MailCategory.General, MailCategorizer.Categorize(M()));

    [Fact]
    public void ListUnsubscribeMeansNewsletter()
        => Assert.Equal(MailCategory.Newsletter,
            MailCategorizer.Categorize(M(listUnsubscribe: "<https://x/unsub>")));

    [Theory]
    [InlineData("bulk")]
    [InlineData("Bulk")]
    [InlineData("junk")]
    public void BulkPrecedenceMeansPromotions(string precedence)
        => Assert.Equal(MailCategory.Promotions,
            MailCategorizer.Categorize(M(precedence: precedence)));

    [Fact]
    public void BulkBeatsNewsletterWhenBothPresent()
    {
        // Werbung trägt oft beides – „bulk" ist die deutlichere Aussage.
        var m = M(listUnsubscribe: "<https://x/unsub>", precedence: "bulk");
        Assert.Equal(MailCategory.Promotions, MailCategorizer.Categorize(m));
    }

    [Fact]
    public void AutoSubmittedMeansNewsletter()
        => Assert.Equal(MailCategory.Newsletter,
            MailCategorizer.Categorize(M(autoSubmitted: "auto-generated")));

    [Fact]
    public void AutoSubmittedNoIsNormalMail()
    {
        // "Auto-Submitted: no" ist der ausdrückliche Hinweis auf echte Post.
        Assert.Equal(MailCategory.General, MailCategorizer.Categorize(M(autoSubmitted: "no")));
    }

    [Theory]
    [InlineData("noreply@shop.de")]
    [InlineData("no-reply@shop.de")]
    [InlineData("no_reply@shop.de")]
    [InlineData("DoNotReply@shop.de")]
    public void NoReplySendersAreNewsletter(string from)
        => Assert.Equal(MailCategory.Newsletter, MailCategorizer.Categorize(M(from)));

    [Theory]
    [InlineData("notification@facebookmail.com")]
    [InlineData("info@linkedin.com")]
    [InlineData("x@mail.instagram.com")]
    public void SocialNetworksGetTheirOwnCategory(string from)
        => Assert.Equal(MailCategory.Social, MailCategorizer.Categorize(M(from)));

    [Fact]
    public void SocialBeatsOtherSignals()
    {
        // Soziale Netzwerke setzen fast immer auch List-Unsubscribe.
        var m = M("notification@facebookmail.com", listUnsubscribe: "<https://fb/unsub>");
        Assert.Equal(MailCategory.Social, MailCategorizer.Categorize(m));
    }

    [Fact]
    public void OverrideBeatsEveryHeuristic()
    {
        var m = M("noreply@shop.de", listUnsubscribe: "<https://x>", precedence: "bulk");
        var overrides = new Dictionary<string, string> { ["noreply@shop.de"] = "General" };

        Assert.Equal(MailCategory.General, MailCategorizer.Categorize(m, overrides));
    }

    [Fact]
    public void UnknownOverrideValueIsIgnored()
    {
        var overrides = new Dictionary<string, string> { ["person@example.org"] = "Quatsch" };
        Assert.Equal(MailCategory.General, MailCategorizer.Categorize(M(), overrides));
    }

    [Fact]
    public void HandlesMissingSenderAddress()
        => Assert.Equal(MailCategory.General, MailCategorizer.Categorize(M(from: "")));

    [Theory]
    [InlineData(MailCategory.General, "Allgemein")]
    [InlineData(MailCategory.Newsletter, "Newsletter")]
    [InlineData(MailCategory.Promotions, "Werbung")]
    [InlineData(MailCategory.Social, "Soziales")]
    public void DisplayNamesAreGerman(MailCategory c, string expected)
        => Assert.Equal(expected, MailCategorizer.DisplayName(c));
}
