using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using NMailClient.Services;

namespace NMailClient.Views;

/// <summary>Durchsuchbare Ansicht des Anhang-Archivs.</summary>
public partial class ArchiveWindow : Window
{
    private readonly AttachmentArchive _archive;

    /// <summary>
    /// Ereignisse aus dem XAML (etwa <c>IsChecked="True"</c>) feuern bereits während
    /// <c>InitializeComponent</c> – also bevor die Felder gesetzt sind. Bis dahin
    /// darf kein Handler auf sie zugreifen.
    /// </summary>
    private readonly bool _ready;

    public ArchiveWindow(AttachmentArchive archive)
    {
        InitializeComponent();
        _archive = archive;
        _ready = true;
        Reload();
    }

    private ArchivedFile? Selected => FileList.SelectedItem as ArchivedFile;

    private void Reload()
    {
        if (!_ready) return;

        var files = _archive.Browse(TxtSearch.Text);

        // Gruppiert nach Absender, oder flach nach Ablagedatum.
        if (TglGroup.IsChecked == true)
        {
            var view = new ListCollectionView(files);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ArchivedFile.Sender)));
            FileList.ItemsSource = view;
        }
        else
        {
            FileList.ItemsSource = files
                .OrderByDescending(f => f.Modified)
                .ToList();
        }

        if (!Directory.Exists(_archive.Root))
        {
            LblInfo.Text = $"Noch nichts abgelegt. Ziel: {_archive.Root}";
            return;
        }

        var total = files.Sum(f => f.Size);
        LblInfo.Text = files.Count == 0
            ? $"Keine Treffer in {_archive.Root}"
            : $"{files.Count} Datei(en), {total / 1024.0 / 1024.0:0.#} MB – {_archive.Root}";
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => Reload();

    private void Group_Toggled(object sender, RoutedEventArgs e) => Reload();

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } file) return;
        Launch(file.FullPath);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        // Bei Auswahl den enthaltenden Ordner zeigen, sonst die Archivwurzel.
        if (Selected is { } file && File.Exists(file.FullPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.FullPath}\""));
            return;
        }

        Directory.CreateDirectory(_archive.Root);
        Launch(_archive.Root);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } file) return;

        var res = MessageBox.Show(this,
            $"'{file.FileName}' endgültig löschen?", "Anhang-Archiv",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res != MessageBoxResult.Yes) return;

        if (!_archive.Delete(file.FullPath))
            MessageBox.Show(this, "Die Datei konnte nicht gelöscht werden.",
                "Anhang-Archiv", MessageBoxButton.OK, MessageBoxImage.Warning);

        Reload();
    }

    private void Launch(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            MessageBox.Show(this, $"Konnte nicht geöffnet werden: {ex.Message}",
                "Anhang-Archiv", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
