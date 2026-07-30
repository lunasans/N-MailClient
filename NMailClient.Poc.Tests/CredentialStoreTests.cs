using NMailClient.Poc.Services;
using Xunit;

namespace NMailClient.Poc.Tests;

/// <summary>
/// Schreibt echte Einträge in den Windows-Anmeldeinformationsverwalter, benutzt aber
/// ausschließlich Ziele mit eindeutiger GUID und räumt sie wieder ab.
/// </summary>
[Trait("Category", "Integration")]
public class CredentialStoreTests : IDisposable
{
    private readonly string _target =
        CredentialStore.TargetFor("test-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => CredentialStore.Delete(_target);

    [Fact]
    public void ReadReturnsNullWhenAbsent()
        => Assert.Null(CredentialStore.Read(_target));

    [Fact]
    public void RoundTripsSecret()
    {
        Assert.True(CredentialStore.Save(_target, "user@example.org", "geheim"));
        Assert.Equal("geheim", CredentialStore.Read(_target));
    }

    [Fact]
    public void RoundTripsUnicodeAndSpecialCharacters()
    {
        const string secret = "P@sswört–mit \"Anführungszeichen\" & Ümlauten € \\ /";

        Assert.True(CredentialStore.Save(_target, "u", secret));
        Assert.Equal(secret, CredentialStore.Read(_target));
    }

    [Fact]
    public void OverwritesExistingSecret()
    {
        CredentialStore.Save(_target, "u", "erstes");
        CredentialStore.Save(_target, "u", "zweites");

        Assert.Equal("zweites", CredentialStore.Read(_target));
    }

    [Fact]
    public void EmptySecretDeletesEntry()
    {
        CredentialStore.Save(_target, "u", "etwas");
        Assert.True(CredentialStore.Save(_target, "u", ""));
        Assert.Null(CredentialStore.Read(_target));
    }

    [Fact]
    public void DeleteIsIdempotent()
    {
        Assert.True(CredentialStore.Delete(_target));
        Assert.True(CredentialStore.Delete(_target));
    }

    [Fact]
    public void TargetIsScopedPerAccountAndPurpose()
    {
        var mail = CredentialStore.TargetFor("abc");
        var api = CredentialStore.TargetFor("abc", "mailcow");

        Assert.NotEqual(mail, api);
        Assert.StartsWith("NMailClient.Poc:", mail);
        Assert.Contains("abc", mail);
    }
}
