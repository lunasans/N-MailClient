using System.IO;
using System.Windows;
using System.Windows.Controls;
using NMailClient.Models;
using NMailClient.Services;
using NMailClient.Services.Dav;

namespace NMailClient.Views;

/// <summary>Kontakte eines CardDAV-Adressbuchs ansehen und bearbeiten.</summary>
public partial class ContactsWindow : Window
{
    private readonly CardDavService _service;
    private readonly Account _account;

    private readonly List<Contact> _all = [];

    /// <summary>Verhindert Rückschreiben, während die Felder gefüllt werden.</summary>
    private bool _filling;

    private Contact? Selected => ContactList.SelectedItem as Contact;

    public ContactsWindow(Account account, CardDavService service)
    {
        InitializeComponent();
        _account = account;
        _service = service;

        Loaded += async (_, _) => await LoadBooksAsync();
    }

    // ---- Laden ---------------------------------------------------------------

    private async Task LoadBooksAsync()
    {
        if (!_service.IsConfigured)
        {
            LblInfo.Text = "Für dieses Konto ist keine CardDAV-Adresse hinterlegt "
                           + "(Einstellungen → Konten).";
            return;
        }

        LblInfo.Text = "Suche Adressbücher …";
        IsEnabled = false;
        try
        {
            var books = await _service.ListAddressBooksAsync();
            CmbBooks.ItemsSource = books;

            if (books.Count == 0)
            {
                LblInfo.Text = "Kein Adressbuch gefunden.";
                return;
            }

            CmbBooks.SelectedIndex = 0; // löst das Laden der Kontakte aus
        }
        catch (Exception ex)
        {
            LblInfo.Text = $"Fehlgeschlagen: {ex.Message}";
            AppLog.Error("Adressbücher konnten nicht geladen werden.", ex);
        }
        finally { IsEnabled = true; }
    }

    private async Task LoadContactsAsync()
    {
        if (CmbBooks.SelectedItem is not DavCollection book) return;

        LblInfo.Text = $"Lade Kontakte aus '{book.DisplayName}' …";
        IsEnabled = false;
        try
        {
            var contacts = await _service.GetContactsAsync(book.Url);

            _all.Clear();
            _all.AddRange(contacts);
            RebuildGroups();
            ApplyFilter();

            LblInfo.Text = $"{_all.Count} Kontakt(e) in '{book.DisplayName}'";
        }
        catch (Exception ex)
        {
            LblInfo.Text = $"Fehlgeschlagen: {ex.Message}";
            AppLog.Error("Kontakte konnten nicht geladen werden.", ex);
        }
        finally { IsEnabled = true; }
    }

    private const string AllGroups = "(alle Gruppen)";

    private void RebuildGroups()
    {
        var groups = _all.SelectMany(c => c.Groups)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var keep = CmbGroups.SelectedItem as string;

        _filling = true;
        try
        {
            CmbGroups.ItemsSource = new[] { AllGroups }.Concat(groups).ToList();
            CmbGroups.SelectedItem = keep is not null && groups.Contains(keep) ? keep : AllGroups;
        }
        finally { _filling = false; }
    }

    private void ApplyFilter()
    {
        var term = TxtSearch.Text;
        var keep = Selected;

        var group = CmbGroups.SelectedItem as string;
        var byGroup = group is null || group == AllGroups
            ? _all
            : _all.Where(c => c.Groups.Contains(group, StringComparer.CurrentCultureIgnoreCase));

        ContactList.ItemsSource = byGroup.Where(c => c.Matches(term)).ToList();

        // Auswahl halten, solange der Kontakt noch im Filter liegt.
        if (keep is not null && ContactList.Items.Contains(keep)) ContactList.SelectedItem = keep;
        else ClearEditor();
    }

    // ---- Bearbeiten ----------------------------------------------------------

    private void Contact_Changed(object sender, SelectionChangedEventArgs e)
    {
        Editor.IsEnabled = Selected is not null;
        if (Selected is not { } contact) return;

        _filling = true;
        try
        {
            TxtDisplay.Text = contact.DisplayName;
            TxtFirst.Text = contact.FirstName;
            TxtLast.Text = contact.LastName;
            TxtOrg.Text = contact.Organization;
            TxtEmails.Text = string.Join(Environment.NewLine, contact.Emails);
            TxtPhones.Text = string.Join(Environment.NewLine, contact.Phones);
            PickBirthday.SelectedDate = contact.Birthday;
            TxtGroups.Text = string.Join(", ", contact.Groups);

            LblContactInfo.Text = contact.IsNew
                ? "Noch nicht gespeichert."
                : $"Serveradresse: {contact.Url}";
        }
        finally { _filling = false; }
    }

    private void ClearEditor()
    {
        _filling = true;
        try
        {
            foreach (var box in new[] { TxtDisplay, TxtFirst, TxtLast, TxtOrg, TxtEmails, TxtPhones, TxtGroups })
                box.Text = "";
            PickBirthday.SelectedDate = null;
            LblContactInfo.Text = "";
        }
        finally { _filling = false; }

        Editor.IsEnabled = false;
    }

    /// <summary>Eingaben in den gewählten Kontakt übernehmen.</summary>
    private void ReadEditor(Contact contact)
    {
        contact.DisplayName = TxtDisplay.Text.Trim();
        contact.FirstName = TxtFirst.Text.Trim();
        contact.LastName = TxtLast.Text.Trim();
        contact.Organization = TxtOrg.Text.Trim();
        contact.Emails = SplitLines(TxtEmails.Text);
        contact.Phones = SplitLines(TxtPhones.Text);
        contact.Birthday = PickBirthday.SelectedDate;
        contact.Groups = TxtGroups.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ---- Import und Export ---------------------------------------------------

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (CmbBooks.SelectedItem is not DavCollection)
        {
            LblInfo.Text = "Bitte zuerst ein Adressbuch wählen.";
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "vCard-Dateien (*.vcf)|*.vcf|Alle Dateien|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        var imported = new List<Contact>();
        foreach (var path in dlg.FileNames)
        {
            try
            {
                imported.AddRange(CardDavService.Parse(File.ReadAllText(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LblInfo.Text = $"'{Path.GetFileName(path)}' nicht lesbar: {ex.Message}";
                return;
            }
        }

        if (imported.Count == 0)
        {
            LblInfo.Text = "Keine Kontakte in der Datei gefunden.";
            return;
        }

        var res = MessageBox.Show(this,
            $"{imported.Count} Kontakt(e) gefunden. Ins Adressbuch übernehmen?",
            "Importieren", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res != MessageBoxResult.Yes) return;

        _ = ImportAsync(imported);
    }

    private async Task ImportAsync(List<Contact> imported)
    {
        if (CmbBooks.SelectedItem is not DavCollection book) return;

        IsEnabled = false;
        int ok = 0, failed = 0;
        try
        {
            foreach (var contact in imported)
            {
                // Als neue Karten anlegen: Adresse und ETag stammen aus der Datei nicht.
                contact.Url = "";
                contact.ETag = null;
                if (string.IsNullOrWhiteSpace(contact.Uid)) contact.Uid = Guid.NewGuid().ToString();

                try { await _service.SaveAsync(book.Url, contact); ok++; }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Warn($"Import von '{contact.Label}' fehlgeschlagen: {ex.Message}");
                }
            }
        }
        finally { IsEnabled = true; }

        LblInfo.Text = failed == 0
            ? $"{ok} Kontakt(e) übernommen."
            : $"{ok} übernommen, {failed} fehlgeschlagen (siehe Protokoll).";

        await LoadContactsAsync();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var shown = ContactList.ItemsSource as IEnumerable<Contact> ?? [];
        var list = shown.ToList();

        if (list.Count == 0)
        {
            LblInfo.Text = "Nichts zu exportieren.";
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "vCard-Dateien (*.vcf)|*.vcf",
            FileName = "kontakte.vcf",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            // Mehrere Karten hintereinander – das ist das übliche Sammelformat.
            var text = string.Concat(list.Select(CardDavService.Serialize));
            File.WriteAllText(dlg.FileName, text);

            LblInfo.Text = $"{list.Count} Kontakt(e) nach '{Path.GetFileName(dlg.FileName)}' gesichert.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LblInfo.Text = $"Export fehlgeschlagen: {ex.Message}";
        }
    }

    private void Group_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        ApplyFilter();
    }

    private static List<string> SplitLines(string text)
        => text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

    // ---- Aktionen ------------------------------------------------------------

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (CmbBooks.SelectedItem is null)
        {
            LblInfo.Text = "Bitte zuerst ein Adressbuch wählen.";
            return;
        }

        var contact = new Contact();
        _all.Add(contact);

        // Ohne Filter-Rücksetzung wäre der leere Kontakt bei aktiver Suche unsichtbar.
        TxtSearch.Text = "";
        ApplyFilter();

        ContactList.SelectedItem = contact;
        TxtDisplay.Focus();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } contact) return;
        if (CmbBooks.SelectedItem is not DavCollection book) return;

        ReadEditor(contact);

        if (string.IsNullOrWhiteSpace(contact.Label) || contact.Label == "(ohne Namen)")
        {
            LblInfo.Text = "Bitte wenigstens einen Namen oder eine Adresse angeben.";
            return;
        }

        LblInfo.Text = "Speichere …";
        IsEnabled = false;
        try
        {
            await _service.SaveAsync(book.Url, contact);

            // Anzeige nachziehen – Contact meldet den Label-Wechsel, die Liste
            // sortiert sich dadurch aber nicht neu.
            ApplyFilter();
            ContactList.SelectedItem = contact;

            LblInfo.Text = $"'{contact.Label}' gespeichert.";
        }
        catch (DavException ex) when (ex.Status == System.Net.HttpStatusCode.PreconditionFailed)
        {
            LblInfo.Text = "Der Kontakt wurde zwischenzeitlich anderswo geändert. "
                           + "Bitte aktualisieren und erneut versuchen.";
        }
        catch (Exception ex)
        {
            LblInfo.Text = $"Speichern fehlgeschlagen: {ex.Message}";
            AppLog.Error("Kontakt konnte nicht gespeichert werden.", ex);
        }
        finally { IsEnabled = true; }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } contact) return;

        var res = MessageBox.Show(this, $"'{contact.Label}' löschen?", "Kontakte",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res != MessageBoxResult.Yes) return;

        IsEnabled = false;
        try
        {
            await _service.DeleteAsync(contact);

            _all.Remove(contact);
            ApplyFilter();
            LblInfo.Text = "Kontakt gelöscht.";
        }
        catch (Exception ex)
        {
            LblInfo.Text = $"Löschen fehlgeschlagen: {ex.Message}";
            AppLog.Error("Kontakt konnte nicht gelöscht werden.", ex);
        }
        finally { IsEnabled = true; }
    }

    private async void Book_Changed(object sender, SelectionChangedEventArgs e)
        => await LoadContactsAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await LoadContactsAsync();

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_filling) return;
        ApplyFilter();
    }
}
