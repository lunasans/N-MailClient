using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace NMailClient.Poc.Services;

public enum AppTheme { Light, Dark, System }

/// <summary>
/// Tauscht das Farb-ResourceDictionary zur Laufzeit. Funktioniert nur, weil alle
/// Styles die Farben per DynamicResource beziehen (siehe Themes/Controls.xaml).
/// </summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.System;

    /// <summary>Tatsächlich aktives Schema (System bereits aufgelöst).</summary>
    public static bool IsDark { get; private set; }

    public static event Action? ThemeChanged;

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        IsDark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsWindowsDark(),
        };

        var uri = new Uri(IsDark
            ? "pack://application:,,,/Themes/Dark.xaml"
            : "pack://application:,,,/Themes/Light.xaml", UriKind.Absolute);
        var dict = new ResourceDictionary { Source = uri };

        var merged = Application.Current.Resources.MergedDictionaries;

        // Das Farb-Dictionary liegt per Konvention an Position 0.
        if (merged.Count == 0) merged.Add(dict);
        else merged[0] = dict;

        Save(theme);
        ThemeChanged?.Invoke();
    }

    public static void Toggle() => Apply(IsDark ? AppTheme.Light : AppTheme.Dark);

    /// <summary>Windows-Einstellung "App-Modus" (AppsUseLightTheme = 0 -&gt; dunkel).</summary>
    private static bool IsWindowsDark()
    {
        try
        {
            var v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return v is int i && i == 0;
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Farbe aus dem aktiven Dictionary – für den HTML-Renderer.
    /// <c>Application.Current</c> ist null, wenn kein WPF-Host läuft (Unit-Tests);
    /// dann greift der Rückfallwert.
    /// </summary>
    public static string HexColor(string key)
    {
        if (Application.Current?.TryFindResource(key) is Color c)
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        return IsDark ? "#1C1F24" : "#FFFFFF";
    }

    // ---- Persistenz --------------------------------------------------------

    /// <summary>
    /// Speicher für die Theme-Wahl. Wird beim Start gesetzt; ohne Anbindung
    /// bleibt die Wahl nur für die laufende Sitzung erhalten.
    /// </summary>
    private static Models.AppSettings? _settings;
    private static Action? _persist;

    public static void Bind(Models.AppSettings settings, Action persist)
    {
        _settings = settings;
        _persist = persist;
    }

    private static void Save(AppTheme theme)
    {
        if (_settings is null) return;
        if (_settings.Theme == theme.ToString()) return;

        _settings.Theme = theme.ToString();
        _persist?.Invoke();
    }

    public static AppTheme LoadPreference()
        => Enum.TryParse<AppTheme>(_settings?.Theme, out var t) ? t : AppTheme.System;
}
