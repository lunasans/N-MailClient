using System.Collections.ObjectModel;
using System.ComponentModel;

namespace NMailClient.Models;

/// <summary>Knoten im Ordnerbaum. Baut sich aus dem IMAP-Delimiter des Servers auf.</summary>
public class FolderNode : INotifyPropertyChanged
{
    public string FullName { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsSelectable { get; init; } = true;
    public ObservableCollection<FolderNode> Children { get; } = new();

    private int _unread;
    public int Unread
    {
        get => _unread;
        set
        {
            if (_unread == value) return;
            _unread = value;
            Raise(nameof(Unread));
            Raise(nameof(HasUnread));
        }
    }

    public bool HasUnread => Unread > 0;

    /// <summary>
    /// Der gemeinsame Posteingang: kein Ordner auf einem Server, sondern die
    /// Posteingänge aller Konten nebeneinander. Steht nur einmal, ganz oben,
    /// und lässt sich nicht umbenennen oder löschen.
    /// </summary>
    public bool IsUnified { get; init; }

    /// <summary>Unterordner starten eingeklappt (wie in der Go-Version, Fix #2).</summary>
    public bool IsExpanded { get; set; }

    /// <summary>Tiefe im Baum – nur für die flache Darstellung im Verschieben-Menü.</summary>
    public int Depth { get; set; }

    public string IndentedName => Depth > 0 ? new string(' ', Depth * 4) + Name : Name;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public override string ToString() => Name;
}
