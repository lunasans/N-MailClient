using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace NMailClient.Services.I18n;

/// <summary>
/// Die Übersetzungen der Oberfläche.
///
/// Der Zugriff läuft über einen <em>Indexer</em>, damit XAML sich daran binden
/// kann: <c>{Binding [Main.Compose], Source={x:Static i18n:Loc.Current}}</c>.
/// Beim Sprachwechsel wird eine Änderung aller Einträge gemeldet, und die
/// Oberfläche stellt sich um — ohne Neustart.
///
/// Fehlt ein Schlüssel, wird er selbst angezeigt. Das ist Absicht: eine leere
/// Schaltfläche wäre schlimmer, und im Test fällt die Lücke sofort auf.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Current { get; } = new();

    private Dictionary<string, string> _strings = [];
    private Dictionary<string, string> _fallback = [];

    private Loc() => Use(DetectSystemLanguage());

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Die Kennung der aktuellen Sprache, etwa „de".</summary>
    public string Language { get; private set; } = "de";

    /// <summary>Alle mitgelieferten Sprachen.</summary>
    public static IReadOnlyList<LanguageInfo> Available =>
    [
        new("de", "Deutsch"),
        new("en", "English"),
    ];

    /// <summary>Der Zugriff, an den XAML sich bindet.</summary>
    public string this[string key] => Get(key);

    /// <summary>Für den Code: kurz und ohne Umweg.</summary>
    public static string T(string key) => Current.Get(key);

    /// <summary>Mit Platzhaltern, etwa <c>T("Main.Count", 5)</c> für „{0} Nachrichten".</summary>
    public static string T(string key, params object[] args)
    {
        var text = Current.Get(key);

        try { return string.Format(CultureInfo.CurrentCulture, text, args); }
        catch (FormatException)
        {
            // Ein falscher Platzhalter im Katalog darf die Anzeige nicht sprengen.
            AppLog.Warn($"Übersetzung '{key}' hat unpassende Platzhalter.");
            return text;
        }
    }

    private string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (_strings.TryGetValue(key, out var value)) return value;

        // Deutsch ist die Rückfallebene: lieber ein deutscher Text als ein
        // roher Schlüssel in der englischen Oberfläche.
        if (_fallback.TryGetValue(key, out var fallback)) return fallback;

        return key;
    }

    public bool Has(string key) => _strings.ContainsKey(key) || _fallback.ContainsKey(key);

    /// <summary>Alle Schlüssel der aktuellen Sprache – für Tests und Werkzeuge.</summary>
    public IReadOnlyCollection<string> Keys => _strings.Keys;

    // ---- Umschalten --------------------------------------------------------

    /// <summary>
    /// Sprache setzen. Unbekannte Kennungen fallen auf Deutsch zurück, statt
    /// eine leere Oberfläche zu erzeugen.
    /// </summary>
    public void Use(string language)
    {
        var wanted = Normalize(language);

        _fallback = Load("de");
        _strings = wanted == "de" ? _fallback : Load(wanted);

        if (_strings.Count == 0)
        {
            _strings = _fallback;
            wanted = "de";
        }

        Language = wanted;

        // Der leere Name meldet „alle Eigenschaften"; damit erneuert WPF jede
        // gebundene Zeichenkette.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    private static string Normalize(string language)
    {
        if (string.IsNullOrWhiteSpace(language)) return "de";

        var value = language.Trim().ToLowerInvariant();
        if (value == "system") return DetectSystemLanguage();

        // „de-AT" und „de" sind für uns dasselbe.
        var dash = value.IndexOf('-');
        return dash > 0 ? value[..dash] : value;
    }

    private static string DetectSystemLanguage()
    {
        var name = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return Available.Any(l => l.Code == name) ? name : "de";
    }

    // ---- Laden -------------------------------------------------------------

    private static Dictionary<string, string> Load(string language)
    {
        var resource = $"NMailClient.Resources.Strings.{language}.json";

        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
            if (stream is null)
            {
                AppLog.Warn($"Sprachdatei '{language}' fehlt.");
                return [];
            }

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            AppLog.Error($"Sprachdatei '{language}' nicht lesbar.", ex);
            return [];
        }
    }
}

public sealed record LanguageInfo(string Code, string Display);
