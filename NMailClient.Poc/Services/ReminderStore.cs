using System.IO;
using System.Text.Json;
using NMailClient.Poc.Models;

namespace NMailClient.Poc.Services;

/// <summary>
/// Lokale Erinnerungen (Snooze und Wiedervorlage). Liegt neben der Konfiguration
/// in einer eigenen Datei – die Einträge sind flüchtiger Natur und sollen
/// <c>db.json</c> nicht aufblähen.
/// </summary>
public class ReminderStore
{
    private readonly string _path;
    private readonly List<Reminder> _items = [];

    public ReminderStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NMailClient.Poc");
        _path = Path.Combine(dir, "reminders.json");
        Load();
    }

    public IReadOnlyList<Reminder> Items
    {
        get { lock (_items) return _items.ToList(); }
    }

    /// <summary>Setzt oder ersetzt die Erinnerung zu einer Nachricht.</summary>
    public void Set(Reminder reminder)
    {
        lock (_items)
        {
            _items.RemoveAll(r => r.Matches(reminder.AccountId, reminder.Folder, reminder.Uid)
                                  && r.Kind == reminder.Kind);
            _items.Add(reminder);
        }
        Save();
    }

    public void Clear(string accountId, string folder, uint uid, ReminderKind? kind = null)
    {
        lock (_items)
            _items.RemoveAll(r => r.Matches(accountId, folder, uid)
                                  && (kind is null || r.Kind == kind));
        Save();
    }

    /// <summary>UIDs, die im angegebenen Ordner derzeit ausgeblendet sind.</summary>
    public HashSet<uint> HiddenUids(string accountId, string folder)
    {
        lock (_items)
            return _items
                .Where(r => r.HidesMessage
                            && string.Equals(r.AccountId, accountId, StringComparison.Ordinal)
                            && string.Equals(r.Folder, folder, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Uid)
                .ToHashSet();
    }

    public Reminder? For(string accountId, string folder, uint uid, ReminderKind kind)
    {
        lock (_items)
            return _items.FirstOrDefault(r => r.Matches(accountId, folder, uid) && r.Kind == kind);
    }

    /// <summary>
    /// Fällige Erinnerungen holen und dabei entfernen – jede meldet sich genau einmal.
    /// Ausgeblendete Nachrichten werden dadurch wieder sichtbar.
    /// </summary>
    public List<Reminder> TakeDue()
    {
        List<Reminder> due;
        lock (_items)
        {
            due = _items.Where(r => r.IsDue).ToList();
            if (due.Count == 0) return due;
            foreach (var r in due) _items.Remove(r);
        }
        Save();
        return due;
    }

    // ---- Persistenz ---------------------------------------------------------

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<Reminder>>(File.ReadAllText(_path));
            if (list is not null) lock (_items) _items.AddRange(list);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            AppLog.Error("Erinnerungen nicht lesbar.", ex);
        }
    }

    private void Save()
    {
        try
        {
            List<Reminder> snapshot;
            lock (_items) snapshot = _items.ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Erinnerungen nicht speicherbar.", ex);
        }
    }
}
