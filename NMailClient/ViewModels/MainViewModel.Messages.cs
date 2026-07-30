using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using NMailClient.Models;
using NMailClient.Services;

namespace NMailClient.ViewModels;

/// <summary>Mailliste: Laden, Suche, Gruppierung, Kategorien, Konversationen.</summary>
public partial class MainViewModel
{
    /// <summary>
    /// Sicht auf <see cref="Messages"/> – trägt die Datumsgruppierung. Die Liste
    /// bindet hierauf, nicht direkt auf die Sammlung.
    /// </summary>
    public ICollectionView MessagesView { get; }

    public double RowSpacing => Settings.RowSpacing;
    public double AvatarSize => Settings.AvatarSize;

    /// <summary>Nach Änderung von Gruppierung oder Dichte aufrufen.</summary>
    public void ApplyListSettings()
    {
        using (MessagesView.DeferRefresh())
        {
            MessagesView.GroupDescriptions.Clear();
            if (Settings.GroupByDate)
                MessagesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MailSummary.DateGroup)));

            // Kategorie-Filter direkt an der Sicht, damit Gruppierung und
            // Virtualisierung erhalten bleiben.
            MessagesView.Filter = Settings.ShowCategories
                ? o => o is MailSummary m && m.Category == SelectedCategory
                : null;
        }

        OnPropertyChanged(nameof(RowSpacing));
        OnPropertyChanged(nameof(AvatarSize));
        OnPropertyChanged(nameof(ShowCategories));
    }

    // ---- Kategorien --------------------------------------------------------

    public bool ShowCategories => Settings.ShowCategories;

    public IReadOnlyList<MailCategory> Categories { get; } =
        [MailCategory.General, MailCategory.Newsletter, MailCategory.Promotions, MailCategory.Social];

    private MailCategory _selectedCategory = MailCategory.General;
    public MailCategory SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!Set(ref _selectedCategory, value)) return;
            MessagesView.Refresh();
            OnPropertyChanged(nameof(CategoryCountsInfo));
        }
    }

    public string CategoryCountsInfo
    {
        get
        {
            if (!Settings.ShowCategories) return "";
            var n = Messages.Count(m => m.Category == SelectedCategory);
            return $"{MailCategorizer.DisplayName(SelectedCategory)}: {n}";
        }
    }

    /// <summary>Absender dauerhaft einer Kategorie zuordnen.</summary>
    public void AssignCategory(MailCategory category)
    {
        var targets = ActionTargets;
        if (targets.Count == 0) return;

        foreach (var m in targets)
        {
            if (string.IsNullOrWhiteSpace(m.FromAddress)) continue;
            Settings.CategoryOverrides[m.FromAddress] = category.ToString();
        }
        _store.Save();

        // Alle Nachrichten desselben Absenders mitziehen, nicht nur die markierten.
        foreach (var m in Messages)
            m.Category = MailCategorizer.Categorize(m, Settings.CategoryOverrides);

        MessagesView.Refresh();
        OnPropertyChanged(nameof(CategoryCountsInfo));
        Status = $"Absender nach '{MailCategorizer.DisplayName(category)}' einsortiert.";
    }


    private bool _isOffline;

    /// <summary>
    /// Die zuletzt gezeigte Liste stammt aus dem Zwischenspeicher, nicht vom
    /// Server. Das gehört sichtbar gemacht: stille Altdaten sind schlimmer als
    /// eine Fehlermeldung.
    /// </summary>
    public bool IsOffline
    {
        get => _isOffline;
        private set
        {
            if (_isOffline == value) return;
            _isOffline = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OfflineHint));
        }
    }

    public string OfflineHint => IsOffline
        ? "Keine Verbindung — angezeigt wird der zuletzt geladene Stand. "
          + "Neue Nachrichten fehlen, Anhänge sind nicht abrufbar."
        : "";

    /// <param name="fresh">
    /// Den Zwischenspeicher überspringen. Zu setzen, wenn feststeht, dass er
    /// veraltet ist — etwa direkt nachdem eine Kopie im Gesendet-Ordner abgelegt
    /// wurde. Sonst erschiene erst der alte Stand ohne die eben gesendete Mail
    /// und würde einen Augenblick später ersetzt; genau das sieht aus wie ein
    /// Nachhinken der Anzeige.
    /// </param>
    public async Task ReloadMessagesAsync(bool fresh = false)
    {
        _offset = 0;
        _unifiedPages = 1;
        _skipCache = fresh;
        Messages.Clear();

        try { await LoadMessagesAsync(); }
        finally { _skipCache = false; }
    }

    private bool _skipCache;

    private bool _loadingMore;
    /// <summary>Läuft gerade ein Nachladen? Steuert Schaltfläche und Hinweis.</summary>
    public bool LoadingMore
    {
        get => _loadingMore;
        private set
        {
            if (!Set(ref _loadingMore, value)) return;
            OnPropertyChanged(nameof(ShowLoadMoreButton));
        }
    }

    public bool ShowLoadMoreButton => CanLoadMore && !LoadingMore;

    /// <summary>
    /// Nächste Seite anhängen.
    ///
    /// Wird beim Scrollen ausgelöst und kann deshalb mehrfach hintereinander
    /// kommen, bevor die erste Anforderung zurück ist — ohne die Sperre stünden
    /// dieselben Nachrichten doppelt in der Liste.
    /// </summary>
    public async Task LoadMoreAsync()
    {
        if (LoadingMore || !CanLoadMore) return;

        LoadingMore = true;
        try { await LoadMessagesAsync(); }
        finally { LoadingMore = false; }
    }

    private async Task LoadMessagesAsync()
    {
        if (SelectedAccount is not { } account || SelectedFolder is not { IsSelectable: true } folder)
        {
            CanLoadMore = false;
            return;
        }

        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        Busy = true;
        var searching = !string.IsNullOrWhiteSpace(SearchText) || FilterUnanswered;

        // Nicht am Offset festmachen: der gemeinsame Eingang zählt in Seiten,
        // nicht in Nachrichten, und liesse ihn unberührt. Nachgeladen wird
        // ausschliesslich über LoadMoreAsync – das ist das verlässliche Merkmal.
        var firstPage = !LoadingMore;

        try
        {
            // Erst zeichnen, was bereits bekannt ist. Der Ordner steht dann
            // sofort da, statt bis zur Antwort des Servers leer zu bleiben.
            // Nur bei der ersten Seite und ohne Suche: "Mehr laden" hängt an,
            // und eine Suche will das Ergebnis des Servers, nicht den Vorrat.
            if (!searching && firstPage && !_skipCache)
            {
                var known = folder.IsUnified
                    ? MergeNewest(Accounts.SelectMany(
                        a => ImapFor(a).CachedMessages(InboxName, 0, PageSize)))
                    : ImapFor(account).CachedMessages(folder.FullName, 0, PageSize);
                if (known.Count > 0)
                {
                    Present(account, folder, [.. known], replace: true);
                    Status = $"{folder.Name}: {Messages.Count} Nachricht(en), wird abgeglichen …";
                }
                else Status = $"Lade {folder.Name} …";
            }

            List<MailSummary> list;
            if (folder.IsUnified)
            {
                // Beim Nachladen wird tiefer geholt und vollständig neu
                // gemischt – deshalb ersetzt das Ergebnis die Liste, statt
                // angehängt zu werden. LoadUnifiedAsync setzt CanLoadMore.
                if (!firstPage) _unifiedPages++;
                list = await LoadUnifiedAsync(cts.Token);
            }
            else if (searching)
            {
                Status = FilterUnanswered && string.IsNullOrWhiteSpace(SearchText)
                    ? "Nur unbeantwortete …"
                    : $"Suche '{SearchText}' …";
                list = await ImapFor(account).SearchAsync(
                    folder.FullName, SearchText.Trim(), FilterUnanswered, cts.Token);
                CanLoadMore = false;
            }
            else
            {
                list = await ImapFor(account).GetMessagesAsync(folder.FullName, _offset, PageSize, cts.Token);
                _offset += list.Count;
                CanLoadMore = list.Count == PageSize;
            }

            if (cts.IsCancellationRequested) return;

            // Der Serverstand ersetzt den vorgezeichneten; beim Nachladen wird
            // angehängt. Der gemeinsame Eingang ersetzt immer, weil er bei jeder
            // Seite vollständig neu gemischt wird.
            Present(account, folder, list,
                replace: folder.IsUnified || (!searching && firstPage));

            IsOffline = ImapFor(account).IsOffline;
            Status = IsOffline
                ? $"{folder.Name}: {Messages.Count} Nachricht(en) aus dem Zwischenspeicher"
                : $"{folder.Name}: {Messages.Count} Nachricht(en)";
        }
        catch (OperationCanceledException)
        {
            // Nutzer hat weitergeklickt – Ergebnis ist obsolet.
        }
        catch (Exception ex)
        {
            Status = $"Fehler: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) Busy = false;
        }
    }


    // ---- Herkunft einer Nachricht ------------------------------------------

    /// <summary>
    /// Das Konto, zu dem eine Nachricht gehört.
    ///
    /// Bis zum gemeinsamen Posteingang war das immer das ausgewählte. Jetzt
    /// trägt jede Zeile ihre Herkunft, und nur die zählt — sonst löschte ein
    /// Klick im falschen Postfach. Der Rückfall auf die Auswahl deckt Zeilen
    /// aus älteren Beständen ab, die noch ohne Vermerk gespeichert wurden.
    /// </summary>
    public Account OwnerOf(MailSummary message, Account fallback)
        => string.IsNullOrEmpty(message.AccountId)
            ? fallback
            : Accounts.FirstOrDefault(a => a.Id == message.AccountId) ?? fallback;

    private static string FolderOf(MailSummary message, FolderNode fallback)
        => MessageGrouping.OriginOf(message, "", fallback.FullName).Folder;

    /// <summary>
    /// Wie <see cref="GroupTargets"/>, aber für Schnellaktionen an einer
    /// einzelnen Zeile: <paramref name="single"/> hat Vorrang vor der Auswahl.
    /// </summary>
    private List<(Account Account, string Folder, List<MailSummary> Messages)> GroupOf(
        MailSummary? single)
    {
        if (single is null) return GroupTargets();
        if (OriginOf(single) is not var (account, folder)) return [];

        return [(account, folder, [single])];
    }

    /// <summary>Konto und Ordner einer Nachricht, bezogen auf die aktuelle Ansicht.</summary>
    public (Account Account, string Folder)? OriginOf(MailSummary? message)
    {
        if (message is null) return null;
        if (SelectedAccount is not { } fallback) return null;
        if (SelectedFolder is not { } folder) return null;

        return (OwnerOf(message, fallback), FolderOf(message, folder));
    }

    /// <summary>
    /// Auswahl nach Herkunft gruppieren. Eine Mehrfachauswahl im gemeinsamen
    /// Eingang kann mehrere Postfächer betreffen; jede Gruppe wird für sich
    /// ausgeführt.
    /// </summary>
    private List<(Account Account, string Folder, List<MailSummary> Messages)> GroupTargets()
    {
        if (SelectedAccount is not { } fallbackAccount) return [];
        if (SelectedFolder is not { } fallbackFolder) return [];

        return [.. MessageGrouping
            .ByOrigin(ActionTargets, fallbackAccount.Id, fallbackFolder.FullName)
            .Select(g => (
                Account: Accounts.FirstOrDefault(a => a.Id == g.Origin.AccountId) ?? fallbackAccount,
                g.Origin.Folder,
                g.Messages))];
    }


    // ---- Gemeinsamer Posteingang -------------------------------------------

    /// <summary>
    /// Die Posteingänge aller Konten nebeneinander, neueste zuerst.
    ///
    /// Die Konten werden gleichzeitig gefragt: jedes hat eine eigene Verbindung,
    /// nacheinander summierten sich sonst die Wartezeiten. Ein Konto, das nicht
    /// antwortet, darf die übrigen nicht aufhalten — sein Fehler wird vermerkt,
    /// und der Rest wird gezeigt.
    /// </summary>
    /// <summary>
    /// Wie viele Seiten der gemeinsame Eingang gerade zeigt. Anders als bei
    /// einem einzelnen Ordner lässt sich hier nicht einfach anhängen: welche
    /// Nachricht auf Platz 51 steht, ergibt sich erst aus der Zusammenführung.
    /// Deshalb wird bei jeder weiteren Seite tiefer geholt und neu gemischt.
    /// </summary>
    private int _unifiedPages = 1;

    private async Task<List<MailSummary>> LoadUnifiedAsync(CancellationToken ct)
    {
        var accounts = Accounts.ToList();
        var depth = _unifiedPages * PageSize;

        var loads = accounts.Select(async a =>
        {
            try { return await ImapFor(a).GetMessagesAsync(InboxName, 0, depth, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn($"Gemeinsamer Posteingang: {a.Email} nicht erreichbar ({ex.Message}).");
                return [];
            }
        }).ToList();

        var results = await Task.WhenAll(loads);

        var failed = results.Count(r => r.Count == 0);
        UnifiedNotice = failed > 0 && failed < accounts.Count
            ? $"{failed} von {accounts.Count} Konten nicht erreichbar."
            : "";

        // Weiter geht es nur, solange mindestens ein Konto noch liefern konnte,
        // was verlangt war – sonst ist überall das Ende erreicht.
        CanLoadMore = results.Any(r => r.Count >= depth);

        return MessageGrouping.MergeNewest(results.SelectMany(r => r), depth);
    }

    /// <summary>
    /// Aus mehreren Postfächern eine Liste: neueste zuerst, auf eine Seite
    /// gekürzt. Ohne die Kürzung stünden bei drei Konten dreimal so viele
    /// Zeilen da wie in jedem einzelnen Ordner.
    /// </summary>
    private List<MailSummary> MergeNewest(IEnumerable<MailSummary> messages)
        => MessageGrouping.MergeNewest(messages, PageSize);

    private string _unifiedNotice = "";
    /// <summary>Hinweis, wenn im gemeinsamen Eingang ein Konto fehlt.</summary>
    public string UnifiedNotice
    {
        get => _unifiedNotice;
        private set { if (Set(ref _unifiedNotice, value)) OnPropertyChanged(nameof(HasUnifiedNotice)); }
    }

    public bool HasUnifiedNotice => !string.IsNullOrEmpty(UnifiedNotice);

    /// <summary>
    /// Eine Liste in die Anzeige übernehmen: zurückgestellte ausblenden,
    /// Konversationen zusammenfassen, Beschriftungen und Kategorie auflösen.
    ///
    /// Gemeinsam für den vorgezeichneten und den abgeglichenen Stand — sonst
    /// liefen zwei Fassungen derselben Regeln nebeneinander und wichen mit der
    /// Zeit voneinander ab.
    /// </summary>
    private void Present(
        Account account, FolderNode folder, List<MailSummary> list, bool replace)
    {
        // Zurückgestellte ausblenden. Die Merker hängen an Konto und Ordner, im
        // gemeinsamen Eingang also je Nachricht verschieden — deshalb wird die
        // Herkunft der Zeile gefragt und nicht die Auswahl.
        var hiddenBy = new Dictionary<(string, string), IReadOnlySet<uint>>();
        IReadOnlySet<uint> HiddenFor(MailSummary m)
        {
            var key = (OwnerOf(m, account).Id, FolderOf(m, folder));
            if (!hiddenBy.TryGetValue(key, out var set))
                hiddenBy[key] = set = _reminders.HiddenUids(key.Item1, key.Item2);
            return set;
        }

        list = list.Where(m => !HiddenFor(m).Contains(m.Uid)).ToList();

        if (Settings.ThreadView) list = CollapseThreads(list);

        // Auswahl merken: der Abgleich darf die geöffnete Nachricht nicht
        // aus der Anzeige schieben.
        var selected = SelectedMessage?.Uid;

        if (replace) Messages.Clear();

        foreach (var m in list)
        {
            var owner = OwnerOf(m, account);
            m.AccountColor = owner.Color;
            m.AccountEmail = owner.Email;

            ResolveLabels(m);
            m.Category = MailCategorizer.Categorize(m, Settings.CategoryOverrides);
            m.FollowUpAt = _reminders.For(
                OwnerOf(m, account).Id, FolderOf(m, folder), m.Uid, ReminderKind.FollowUp)?.DueAt;
            Messages.Add(m);
        }

        OnPropertyChanged(nameof(CategoryCountsInfo));

        if (replace && selected is { } uid)
        {
            var again = Messages.FirstOrDefault(m => m.Uid == uid);
            // Ohne Umweg über die Eigenschaft: ein erneutes Laden des Körpers
            // wäre überflüssig, die Nachricht ist dieselbe.
            if (again is not null) RestoreSelection(again);
        }
    }

    /// <summary>
    /// Auswahl wiederherstellen, ohne den Nachrichtenkörper neu zu holen.
    /// </summary>
    private void RestoreSelection(MailSummary message)
    {
        if (_selectedMessage?.Uid == message.Uid)
        {
            _selectedMessage = message;
            OnPropertyChanged(nameof(SelectedMessage));
        }
        else SelectedMessage = message;
    }


    // ---- Konversationsansicht ----------------------------------------------

    /// <summary>
    /// Fasst jede Konversation auf ihre jüngste Nachricht zusammen und vermerkt
    /// die Anzahl. Bewusst nur eine Zusammenfassung, kein aufklappbarer Baum –
    /// das Öffnen zeigt weiterhin genau eine Nachricht.
    /// </summary>
    private static List<MailSummary> CollapseThreads(List<MailSummary> list)
    {
        var result = new List<MailSummary>();
        foreach (var group in ThreadBuilder.Build(list))
        {
            var newest = group[^1];  // Build() sortiert je Gruppe aufsteigend
            newest.ThreadCount = group.Count;

            // Ungelesen und markiert gelten für den ganzen Thread, sonst wirkt eine
            // Konversation gelesen, obwohl eine ältere Nachricht darin es nicht ist.
            if (group.Any(m => !m.Seen)) newest.Seen = false;
            if (group.Any(m => m.Flagged)) newest.Flagged = true;

            result.Add(newest);
        }
        return result;
    }

}
