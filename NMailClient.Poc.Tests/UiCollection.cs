using Xunit;

namespace NMailClient.Poc.Tests;

/// <summary>
/// Sammlung für alles, was am globalen Zustand der Oberfläche rührt.
///
/// <see cref="NMailClient.Poc.Services.I18n.Loc"/> ist ein Singleton, und
/// mehrere Testklassen schalten daran die Sprache um. xUnit lässt Testklassen
/// standardmässig <b>parallel</b> laufen — dabei traten sie sich gegenseitig auf
/// die Füsse: mal schlug die Sprachprüfung fehl, mal der Fensterbau, und beim
/// nächsten Durchlauf war alles grün.
///
/// Sporadisches Rot ist schlimmer als nutzlos: es erzieht dazu, Fehlschläge zu
/// übersehen. Klassen derselben Sammlung laufen nacheinander, damit ist der
/// gemeinsame Zustand wieder eindeutig.
///
/// Dasselbe gilt für die Fenstertests: WPF erlaubt nur <b>eine</b>
/// <c>Application</c> je Prozess, und deren Ressourcen hängen am erzeugenden
/// Thread.
/// </summary>
[CollectionDefinition(Name)]
public class UiCollection
{
    public const string Name = "Oberflaeche";
}
