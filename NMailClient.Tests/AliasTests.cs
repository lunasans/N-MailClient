using System.Text.Json;
using NMailClient.Models;
using Xunit;

namespace NMailClient.Tests;

public class AliasTests
{
    [Fact]
    public void SenderOptionsStartWithTheAccountItself()
    {
        var a = new Account
        {
            Name = "Rene", Email = "rene@example.org", Signature = "Konto-Signatur",
            Aliases = [new AliasDef { Address = "info@example.org" }],
        };

        var options = a.SenderOptions.ToList();

        Assert.Equal(2, options.Count);
        Assert.Equal("rene@example.org", options[0].Address);
        Assert.Equal("Konto-Signatur", options[0].Signature);
        Assert.Equal("info@example.org", options[1].Address);
    }

    [Fact]
    public void AccountWithoutAliasesOffersOnlyItself()
        => Assert.Single(new Account { Email = "a@b.c" }.SenderOptions);

    [Theory]
    [InlineData("", "info@example.org", "info@example.org")]
    [InlineData("Info", "info@example.org", "Info <info@example.org>")]
    public void DisplayFallsBackToTheAddress(string name, string address, string expected)
        => Assert.Equal(expected, new AliasDef { Name = name, Address = address }.Display);

    [Fact]
    public void AliasesSurviveRoundTrip()
    {
        var a = new Account
        {
            Email = "a@b.c",
            Aliases = [new AliasDef { Address = "x@y.z", Name = "X", Signature = "Sig" }],
        };

        var back = JsonSerializer.Deserialize<Account>(JsonSerializer.Serialize(a))!;

        var alias = Assert.Single(back.Aliases);
        Assert.Equal("x@y.z", alias.Address);
        Assert.Equal("Sig", alias.Signature);
    }

    [Fact]
    public void DerivedSenderOptionsAreNotPersisted()
    {
        var json = JsonSerializer.Serialize(new Account { Email = "a@b.c" });

        Assert.DoesNotContain("SenderOptions", json);
        Assert.Contains("aliases", json);
    }
}
