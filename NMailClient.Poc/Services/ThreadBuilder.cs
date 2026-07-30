using System.Text.RegularExpressions;
using NMailClient.Poc.Models;

namespace NMailClient.Poc.Services;

/// <summary>
/// Fasst Nachrichten zu Konversationen zusammen.
///
/// Grundlage sind <c>Message-ID</c>, <c>In-Reply-To</c> und <c>References</c>
/// (RFC 5322) – nicht der Betreff wie in der Go-Version. Betreffgleichheit ist
/// unzuverlässig: „Rechnung" von zwei Absendern ist keine Konversation, und eine
/// Antwort mit geändertem Betreff gehört trotzdem dazu.
///
/// Ohne diese Header (manche Absender setzen sie nicht) greift als Rückfall die
/// Betreffgleichheit nach Entfernen der Präfixe.
/// </summary>
public static class ThreadBuilder
{
    private static readonly Regex ReplyPrefix = new(
        @"^\s*((re|aw|antw|fwd|fw|wg)\s*(\[\d+\])?\s*:\s*)+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Betreff ohne „Re:"/„AW:"/„Fwd:"-Präfixe, für den Rückfallpfad.</summary>
    public static string NormalizeSubject(string subject)
        => ReplyPrefix.Replace(subject ?? "", "").Trim();

    /// <summary>
    /// Gruppiert die Nachrichten. Jede Gruppe ist nach Datum aufsteigend sortiert,
    /// die Gruppen selbst nach ihrer jüngsten Nachricht absteigend – wie eine
    /// Mailliste, nur eben je Konversation.
    /// </summary>
    public static List<List<MailSummary>> Build(IEnumerable<MailSummary> messages)
    {
        var list = messages.ToList();

        // Union-Find über Message-IDs: jede Nachricht wird mit allen IDs
        // verbunden, die sie referenziert.
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string Find(string x)
        {
            if (!parent.TryGetValue(x, out var p)) { parent[x] = x; return x; }
            if (!string.Equals(p, x, StringComparison.OrdinalIgnoreCase))
                parent[x] = Find(p);
            return parent[x];
        }

        void Union(string a, string b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (!string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase)) parent[ra] = rb;
        }

        // Schlüssel je Nachricht: eigene ID, sonst ein künstlicher Wert.
        var keyOf = new Dictionary<MailSummary, string>();
        foreach (var m in list)
        {
            var own = !string.IsNullOrWhiteSpace(m.MessageId)
                ? m.MessageId!
                : $"uid:{m.Uid}";
            keyOf[m] = own;
            Find(own);

            foreach (var reference in m.ThreadReferences)
                if (!string.IsNullOrWhiteSpace(reference)) Union(own, reference);
        }

        // Rückfall über den Betreff – nur für Nachrichten ganz ohne Header-Information.
        // Eine eigene Message-ID genügt: dann ist die Nachricht entweder Wurzel eines
        // Threads oder gehört per References dazu. Sie zusätzlich über den Betreff
        // zusammenzuziehen würde gleichnamige, unverwandte Mails verschmelzen.
        var bySubject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in list)
        {
            if (!string.IsNullOrWhiteSpace(m.MessageId) || m.ThreadReferences.Count > 0)
                continue;

            var subject = NormalizeSubject(m.Subject);
            if (subject.Length == 0) continue;

            if (bySubject.TryGetValue(subject, out var other)) Union(keyOf[m], other);
            else bySubject[subject] = keyOf[m];
        }

        return list
            .GroupBy(m => Find(keyOf[m]), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(m => m.Date).ToList())
            .OrderByDescending(g => g.Max(m => m.Date))
            .ToList();
    }
}
