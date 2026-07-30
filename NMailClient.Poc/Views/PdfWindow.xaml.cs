using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using NMailClient.Poc.Services;

namespace NMailClient.Poc.Views;

/// <summary>
/// PDF-Vorschau im eingebauten Betrachter. WebView2 bringt den Chromium-Viewer
/// mit – ein eigener PDF-Renderer oder ein Fremdpaket wäre dafür unnötig.
/// </summary>
public partial class PdfWindow : Window
{
    private readonly string _path;
    private readonly string _fileName;

    public PdfWindow(string path, string fileName)
    {
        InitializeComponent();
        _path = path;
        _fileName = fileName;

        Title = $"PDF-Vorschau – {fileName}";
        LblInfo.Text = fileName;

        Loaded += async (_, _) => await ShowAsync();
    }

    private async Task ShowAsync()
    {
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NMailClient.Poc", "WebView2");

            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await Viewer.EnsureCoreWebView2Async(env);

            var settings = Viewer.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsStatusBarEnabled = false;

            Viewer.CoreWebView2.Navigate(new Uri(_path).AbsoluteUri);
        }
        catch (Exception ex)
        {
            LblInfo.Text = $"Vorschau nicht möglich: {ex.Message}";
            AppLog.Error("PDF-Vorschau fehlgeschlagen.", ex);
        }
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            LblInfo.Text = $"Konnte nicht geöffnet werden: {ex.Message}";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = _fileName,
            Filter = "PDF-Dateien (*.pdf)|*.pdf|Alle Dateien|*.*",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            File.Copy(_path, dlg.FileName, overwrite: true);
            LblInfo.Text = $"Gespeichert: {dlg.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LblInfo.Text = $"Speichern fehlgeschlagen: {ex.Message}";
        }
    }
}
