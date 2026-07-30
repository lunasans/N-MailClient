using System.Windows.Data;
using System.Windows.Markup;

namespace NMailClient.Services.I18n;

/// <summary>
/// Kurzschreibweise für übersetzte Texte in XAML: <c>{i18n:T Main.Toolbar.Compose}</c>.
///
/// Liefert bewusst eine <see cref="Binding"/> und keinen festen Wert — nur so
/// stellt sich die Oberfläche beim Sprachwechsel um, ohne dass die Anwendung
/// neu gestartet werden muss.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Current,
            Mode = BindingMode.OneWay,

            // Beim Sprachwechsel meldet Loc eine Änderung an "Item[]"; damit
            // erneuert WPF jede dieser Bindungen.
            FallbackValue = Key,
        };

        return binding.ProvideValue(serviceProvider);
    }
}
