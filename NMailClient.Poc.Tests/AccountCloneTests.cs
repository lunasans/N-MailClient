using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using NMailClient.Poc.Models;
using Xunit;

namespace NMailClient.Poc.Tests;

/// <summary>
/// <see cref="Account.Clone"/> wurde eingeführt, weil dieselben Felder an drei
/// Stellen von Hand kopiert wurden – jedes neue Feld ging dort still verloren
/// (zuletzt die Ordner-Reihenfolge und die Aliase).
/// </summary>
public class AccountCloneTests
{
    private static Account Full() => new()
    {
        Id = "acc-1", Name = "Rene", Email = "rene@example.org", User = "rene",
        Password = "geheim",
        ImapHost = "imap.example.org", ImapPort = 993,
        SmtpHost = "smtp.example.org", SmtpPort = 465,
        Signature = "Viele Grüße", Color = "#123456",
        CardDavUrl = "https://dav.example.org/carddav/",
        CalDavUrl = "https://dav.example.org/caldav/",
        FolderOrder = ["Archiv", "Entwürfe"],
        Aliases = [new AliasDef { Address = "info@example.org", Name = "Info", Signature = "Team" }],
    };

    /// <summary>
    /// Der eigentliche Schutz: jedes serialisierte Feld muss die Kopie überleben.
    /// Ein neues Feld, das in Clone() fehlt, lässt diesen Test scheitern – ohne
    /// dass jemand daran denken muss, den Test zu erweitern.
    /// </summary>
    [Fact]
    public void CloneCarriesEveryPersistedField()
    {
        var original = Full();
        var copy = original.Clone();

        var missing = new List<string>();
        foreach (var property in typeof(Account).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not null) continue;

            var a = JsonSerializer.Serialize(property.GetValue(original));
            var b = JsonSerializer.Serialize(property.GetValue(copy));
            if (a != b) missing.Add(property.Name);
        }

        Assert.True(missing.Count == 0,
            "Diese Felder fehlen in Account.Clone(): " + string.Join(", ", missing));
    }

    [Fact]
    public void FolderOrderIsCopiedNotShared()
    {
        var original = Full();
        var copy = original.Clone();

        copy.FolderOrder.Add("Neu");

        Assert.Equal(2, original.FolderOrder.Count);
    }

    [Fact]
    public void AliasObjectsAreCopiedNotShared()
    {
        // Der Konten-Editor verändert Alias-Objekte direkt; ohne tiefe Kopie
        // schlüge das sofort aufs Originalkonto durch.
        var original = Full();
        var copy = original.Clone();

        copy.Aliases[0].Address = "geaendert@example.org";

        Assert.Equal("info@example.org", original.Aliases[0].Address);
    }

    [Fact]
    public void CloneIsIndependentForSimpleFields()
    {
        var original = Full();
        var copy = original.Clone();

        copy.Email = "anders@example.org";

        Assert.Equal("rene@example.org", original.Email);
    }
}
