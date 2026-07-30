using System.Windows;

namespace NMailClient.Views;

/// <summary>
/// Einzeilige Eingabe – WPF bringt kein Pendant zu einer InputBox mit.
/// Wird für Ordnernamen verwendet.
/// </summary>
public partial class PromptWindow : Window
{
    private readonly Func<string, string?>? _validate;

    public string Value => TxtValue.Text.Trim();

    /// <param name="validate">
    /// Liefert eine Fehlermeldung oder null, wenn die Eingabe in Ordnung ist.
    /// </param>
    public PromptWindow(string title, string prompt, string initial = "",
        Func<string, string?>? validate = null)
    {
        InitializeComponent();
        Title = title;
        LblPrompt.Text = prompt;
        TxtValue.Text = initial;
        _validate = validate;

        Loaded += (_, _) =>
        {
            TxtValue.Focus();
            TxtValue.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var error = _validate?.Invoke(Value)
                    ?? (Value.Length == 0 ? "Bitte einen Wert eingeben." : null);

        if (error is not null)
        {
            LblError.Text = error;
            LblError.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }

    /// <summary>Bequemer Aufruf; null bedeutet abgebrochen.</summary>
    public static string? Ask(Window owner, string title, string prompt,
        string initial = "", Func<string, string?>? validate = null)
    {
        var dlg = new PromptWindow(title, prompt, initial, validate) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }
}
