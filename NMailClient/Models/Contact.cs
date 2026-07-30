using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NMailClient.Models;

/// <summary>
/// Ein Kontakt aus einem CardDAV-Adressbuch.
///
/// <see cref="Url"/> und <see cref="ETag"/> stammen vom Server: die Adresse
/// identifiziert die Karte, das ETag schützt beim Speichern vor dem Überschreiben
/// fremder Änderungen.
/// </summary>
public class Contact : INotifyPropertyChanged
{
    public string Url { get; set; } = "";
    public string? ETag { get; set; }

    /// <summary>UID aus der vCard; bleibt über Änderungen hinweg gleich.</summary>
    public string Uid { get; set; } = "";

    private string _displayName = "";
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }

    private string _firstName = "";
    public string FirstName { get => _firstName; set => Set(ref _firstName, value); }

    private string _lastName = "";
    public string LastName { get => _lastName; set => Set(ref _lastName, value); }

    private string _organization = "";
    public string Organization { get => _organization; set => Set(ref _organization, value); }

    public List<string> Emails { get; set; } = [];
    public List<string> Phones { get; set; } = [];

    private DateTime? _birthday;
    public DateTime? Birthday { get => _birthday; set => Set(ref _birthday, value); }

    /// <summary>
    /// Gruppen bzw. Verteiler, denen der Kontakt angehört (vCard-Feld CATEGORIES).
    /// Bewusst über Kategorien statt über vCard-4-Gruppenkarten: das verstehen
    /// auch ältere Clients, und ein Verteiler ist damit einfach eine Auswahl.
    /// </summary>
    public List<string> Groups { get; set; } = [];

    /// <summary>Neu angelegt und noch nicht auf dem Server?</summary>
    public bool IsNew => string.IsNullOrEmpty(Url);

    /// <summary>Anzeigename mit Rückfall auf Namensteile, Organisation, Adresse.</summary>
    public string Label
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DisplayName)) return DisplayName;

            var name = $"{FirstName} {LastName}".Trim();
            if (name.Length > 0) return name;

            if (!string.IsNullOrWhiteSpace(Organization)) return Organization;
            return Emails.FirstOrDefault() ?? "(ohne Namen)";
        }
    }

    public string PrimaryEmail => Emails.FirstOrDefault() ?? "";

    /// <summary>Darstellung für Empfängerfelder: „Name &lt;adresse&gt;“.</summary>
    public string AsRecipient => string.IsNullOrWhiteSpace(PrimaryEmail)
        ? ""
        : string.IsNullOrWhiteSpace(Label) || Label == PrimaryEmail
            ? PrimaryEmail
            : $"{Label} <{PrimaryEmail}>";

    /// <summary>Freitextsuche über alle sichtbaren Felder.</summary>
    public bool Matches(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return true;

        return Contains(Label, term) || Contains(Organization, term)
            || Emails.Any(e => Contains(e, term))
            || Phones.Any(p => Contains(p, term));

        static bool Contains(string? value, string term)
            => value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => Label;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
    }
}
