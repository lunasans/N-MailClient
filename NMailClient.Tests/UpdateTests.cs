using NMailClient.Services.Update;
using Xunit;

namespace NMailClient.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v1.2.3", 1, 2, 3, null)]
    [InlineData("V1.2.3", 1, 2, 3, null)]
    [InlineData("0.7.0", 0, 7, 0, null)]
    [InlineData("1.2", 1, 2, 0, null)]
    [InlineData("2", 2, 0, 0, null)]
    [InlineData("1.2.3-beta.2", 1, 2, 3, "beta.2")]
    [InlineData("v0.7.0-rc1", 0, 7, 0, "rc1")]
    [InlineData("1.2.3+build.77", 1, 2, 3, null)]
    [InlineData("1.2.3-beta+build", 1, 2, 3, "beta")]
    [InlineData("  1.2.3  ", 1, 2, 3, null)]
    public void ParsesTheUsualTagShapes(
        string text, int major, int minor, int patch, string? pre)
    {
        var version = AppVersion.Parse(text);

        Assert.NotNull(version);
        Assert.Equal(major, version.Value.Major);
        Assert.Equal(minor, version.Value.Minor);
        Assert.Equal(patch, version.Value.Patch);
        Assert.Equal(pre, version.Value.PreRelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("keine Version")]
    [InlineData("1.x.3")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.3.4.5")]
    public void RubbishIsRejected(string? text) => Assert.Null(AppVersion.Parse(text));

    [Fact]
    public void NumbersAreComparedNumericallyNotAsText()
    {
        // Der klassische Fehler: als Zeichenkette wäre 0.10.0 kleiner als 0.9.0.
        var ten = AppVersion.Parse("0.10.0")!.Value;
        var nine = AppVersion.Parse("0.9.0")!.Value;

        Assert.True(ten > nine);
        Assert.False(nine > ten);
    }

    [Theory]
    [InlineData("2.0.0", "1.9.9")]
    [InlineData("1.1.0", "1.0.9")]
    [InlineData("1.0.1", "1.0.0")]
    [InlineData("1.0.0", "1.0.0-beta")]        // fertig schlägt Vorabversion
    [InlineData("1.0.0-beta.2", "1.0.0-beta.1")]
    [InlineData("1.0.0-beta.10", "1.0.0-beta.2")]  // auch hier numerisch
    [InlineData("1.0.0-beta", "1.0.0-alpha")]
    [InlineData("1.0.0-beta.1", "1.0.0-beta")]     // längere Kennung ist grösser
    [InlineData("1.0.0-alpha.beta", "1.0.0-alpha.1")]  // Text schlägt Zahl
    public void OrderingIsCorrect(string bigger, string smaller)
    {
        var a = AppVersion.Parse(bigger)!.Value;
        var b = AppVersion.Parse(smaller)!.Value;

        Assert.True(a > b, $"{a} sollte grösser als {b} sein");
        Assert.True(b < a);
    }

    [Fact]
    public void EqualVersionsAreEqual()
    {
        var a = AppVersion.Parse("v1.2.3")!.Value;
        var b = AppVersion.Parse("1.2.3")!.Value;

        Assert.Equal(0, a.CompareTo(b));
        Assert.True(a >= b);
        Assert.True(a <= b);
    }

    [Fact]
    public void BuildMetadataDoesNotAffectOrdering()
    {
        var a = AppVersion.Parse("1.2.3+links")!.Value;
        var b = AppVersion.Parse("1.2.3+rechts")!.Value;

        Assert.Equal(0, a.CompareTo(b));
    }

    [Fact]
    public void DisplayRoundTrips()
    {
        Assert.Equal("1.2.3", AppVersion.Parse("v1.2.3")!.Value.ToString());
        Assert.Equal("1.2.3-rc1", AppVersion.Parse("v1.2.3-rc1")!.Value.ToString());
    }

    [Fact]
    public void CurrentVersionComesFromTheApplicationNotTheHost()
    {
        // Der Wert stammt aus <Version> in der Projektdatei der Anwendung.
        // Läuft der Test unter einem fremden Wirt (Test-Host), darf hier nicht
        // dessen Version herauskommen — genau das war anfangs der Fall und
        // machte diesen Test wertlos.
        var current = AppVersion.Current;
        var expected = AppVersion.Parse(
            typeof(AppVersion).Assembly.GetName().Version?.ToString())!.Value;

        Assert.Equal(expected.Major, current.Major);
        Assert.Equal(expected.Minor, current.Minor);
        Assert.True(current > AppVersion.Parse("0.0.0")!.Value,
                    $"Version {current} sieht nach einem fehlenden Eintrag aus");

        // Bewusst keine feste Zahl: die stünde dann an einer zweiten Stelle und
        // müsste bei jeder Erhöhung mitgepflegt werden. Geprüft wird, dass der
        // Wert überhaupt aus der Anwendung stammt — nicht welcher es ist.
    }
}

public class UpdateEvaluationTests
{
    private static readonly AppVersion Installed = AppVersion.Parse("0.6.0")!.Value;

    private static string Release(string tag, bool draft = false, bool pre = false) => $$"""
        {
          "tag_name": "{{tag}}",
          "name": "Ausgabe {{tag}}",
          "html_url": "https://github.com/lunasans/N-MailClient/releases/tag/{{tag}}",
          "body": "Neuerungen und Behebungen.",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(pre ? "true" : "false")}},
          "published_at": "2026-07-01T10:00:00Z"
        }
        """;

    [Fact]
    public void NewerReleaseIsOffered()
    {
        var info = UpdateService.Evaluate(Installed, Release("v0.7.0"));

        Assert.Equal(UpdateState.Available, info.State);
        Assert.Equal("0.7.0", info.Latest?.ToString());
        Assert.Contains("0.7.0", info.Summary);
        Assert.Equal("https://github.com/lunasans/N-MailClient/releases/tag/v0.7.0", info.ReleaseUrl);
        Assert.Equal("Neuerungen und Behebungen.", info.Notes);
    }

    [Fact]
    public void SameVersionIsUpToDate()
    {
        var info = UpdateService.Evaluate(Installed, Release("v0.6.0"));

        Assert.Equal(UpdateState.UpToDate, info.State);
        Assert.Contains("aktuell", info.Summary);
    }

    [Fact]
    public void OlderReleaseIsNotOffered()
    {
        // Kommt vor, wenn jemand eine Vorabfassung baut, die neuer ist als das
        // letzte Release.
        var info = UpdateService.Evaluate(Installed, Release("v0.5.0"));

        Assert.Equal(UpdateState.UpToDate, info.State);
    }

    [Fact]
    public void DraftsAreIgnored()
    {
        var info = UpdateService.Evaluate(Installed, Release("v9.9.9", draft: true));

        Assert.Equal(UpdateState.Failed, info.State);
        Assert.DoesNotContain("9.9.9", info.Summary);
    }

    [Fact]
    public void PreReleasesAreNotOffered()
    {
        // Wer Vorabversionen will, holt sie selbst — sie ungefragt anzubieten
        // wäre eine Zumutung.
        var info = UpdateService.Evaluate(Installed, Release("v0.7.0-rc1", pre: true));

        Assert.Equal(UpdateState.UpToDate, info.State);
    }

    [Fact]
    public void PublishDateIsCarriedThrough()
    {
        var info = UpdateService.Evaluate(Installed, Release("v0.7.0"));

        Assert.Equal(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero), info.Published);
    }

    [Fact]
    public void UnreadableTagIsReportedAsFailure()
    {
        var info = UpdateService.Evaluate(Installed, Release("irgendwas"));

        Assert.Equal(UpdateState.Failed, info.State);
        Assert.Contains("irgendwas", info.Error);
    }

    [Theory]
    [InlineData("kein json")]
    [InlineData("{")]
    [InlineData("[]")]
    public void BrokenAnswersBecomeAFailureNotAnException(string json)
    {
        var info = UpdateService.Evaluate(Installed, json);

        Assert.Equal(UpdateState.Failed, info.State);
        Assert.NotNull(info.Error);
    }

    [Fact]
    public void UnknownStateShowsOnlyTheInstalledVersion()
    {
        var info = UpdateInfo.Unknown(Installed);

        Assert.Equal(UpdateState.Unknown, info.State);
        Assert.Equal("Version 0.6.0", info.Summary);
    }
}

/// <summary>
/// Fragt wirklich bei GitHub nach. Standardmässig ausgeschlossen — aber ohne
/// das wüsste niemand, ob die Kennung stimmt und die Antwort passt.
/// </summary>
[Trait("Category", "Network")]
public class UpdateNetworkTests
{
    [Fact]
    public async Task GitHubAnswersAndTheReplyIsUnderstood()
    {
        var info = await new UpdateService().CheckAsync(
            ct: TestContext.Current.CancellationToken);

        // Welcher Zustand herauskommt, hängt vom Stand des Archivs ab; wichtig
        // ist, dass es kein Fehler ist — das wäre ein Zeichen für eine falsche
        // Kennung, eine fehlende User-Agent-Zeile oder ein geändertes Format.
        Assert.NotEqual(UpdateState.Failed, info.State);
        Assert.NotNull(info.Latest);
    }

    [Fact]
    public async Task AnUnknownRepositoryFailsGracefully()
    {
        var info = await new UpdateService().CheckAsync(
            "lunasans/gibt-es-ganz-sicher-nicht", TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Failed, info.State);
        Assert.Contains("404", info.Error);
    }
}
