using System.Windows;

namespace NMailClient.Poc.Views;

/// <summary>Rohansicht einer Nachricht (Header + Body).</summary>
public partial class SourceWindow : Window
{
    public SourceWindow(string subject, string raw)
    {
        InitializeComponent();
        Title = $"Quelltext – {subject}";
        TxtSource.Text = raw;
        LblInfo.Text = $"{raw.Length:N0} Zeichen";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(TxtSource.Text);
        LblInfo.Text = "In die Zwischenablage kopiert.";
    }
}
