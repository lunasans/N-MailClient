using System.Text.Json;
using NMailClient.Poc.Models;
using NMailClient.Poc.Services;
using Xunit;

namespace NMailClient.Poc.Tests;

public class TranslateServiceTests
{
    [Fact]
    public void WithoutUrlTheFeatureIsOff()
        => Assert.False(new TranslateService(() => ("", "")).IsConfigured);

    [Fact]
    public void WhitespaceUrlCountsAsUnconfigured()
        => Assert.False(new TranslateService(() => ("   ", "key")).IsConfigured);

    [Fact]
    public void WithUrlTheFeatureIsAvailable()
        => Assert.True(new TranslateService(() => ("https://translate.example.org", "")).IsConfigured);

    [Fact]
    public async Task TranslatingWithoutUrlFailsWithAClearMessage()
    {
        var service = new TranslateService(() => ("", ""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranslateAsync("Hallo", "en", isHtml: false,
                TestContext.Current.CancellationToken));

        Assert.Contains("Einstellungen", ex.Message);
    }

    [Fact]
    public void ConfigurationIsReadPerCall()
    {
        // Änderungen an Adresse und Schlüssel sollen ohne Neustart greifen.
        var url = "";
        var service = new TranslateService(() => (url, ""));

        Assert.False(service.IsConfigured);
        url = "https://translate.example.org";
        Assert.True(service.IsConfigured);
    }
}

public class TranslateSettingsTests
{
    [Fact]
    public void DefaultsToGermanAndDisabled()
    {
        var s = new AppSettings();

        Assert.Equal("de", s.TranslateTarget);
        Assert.Equal("", s.TranslateUrl);
    }

    [Fact]
    public void ApiKeyIsNotStoredInSettings()
    {
        var json = JsonSerializer.Serialize(new AppSettings
        {
            TranslateUrl = "https://translate.example.org",
        });

        Assert.Contains("translateUrl", json);
        // Der Schlüssel gehört in den Anmeldeinformationsverwalter, nicht in die Datei.
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("translateKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsSurviveRoundTrip()
    {
        var s = new AppSettings { TranslateUrl = "https://t.example.org", TranslateTarget = "en" };
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;

        Assert.Equal("https://t.example.org", back.TranslateUrl);
        Assert.Equal("en", back.TranslateTarget);
    }
}

public class GlobalCredentialTargetTests
{
    [Fact]
    public void GlobalTargetIsDistinctFromAccountTargets()
    {
        var global = CredentialStore.GlobalTarget("translate");
        var account = CredentialStore.TargetFor("translate");

        Assert.NotEqual(global, account);
        Assert.StartsWith("NMailClient.Poc:", global);
    }
}
