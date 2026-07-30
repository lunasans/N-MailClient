namespace NMailClient.Poc.Services.Shell;

/// <summary>
/// Ruhezeiten: in diesem Zeitfenster wird nicht gemeldet.
///
/// Reine Rechnerei ohne Systemzugriff, damit sie prüfbar bleibt. Der einzige
/// Stolperstein ist das Fenster über Mitternacht (22:00–07:00) — das ist der
/// Normalfall und nicht die Ausnahme.
/// </summary>
public readonly record struct QuietHours(bool Enabled, TimeOnly From, TimeOnly To)
{
    public static readonly QuietHours Off = new(false, new TimeOnly(22, 0), new TimeOnly(7, 0));

    /// <summary>Ist zu diesem Zeitpunkt Ruhe?</summary>
    public bool IsQuiet(TimeOnly now)
    {
        if (!Enabled) return false;

        // Gleicher Anfang und Ende hiesse „24 Stunden Ruhe"; das ist mit
        // Sicherheit nicht gemeint, wenn jemand die Zeiten versehentlich
        // gleichsetzt — also gilt dann keine Ruhezeit.
        if (From == To) return false;

        return From < To
            ? now >= From && now < To          // innerhalb eines Tages
            : now >= From || now < To;         // über Mitternacht hinweg
    }

    public bool IsQuiet(DateTime now) => IsQuiet(TimeOnly.FromDateTime(now));

    public string Display => Enabled
        ? $"Ruhe von {From:HH\\:mm} bis {To:HH\\:mm}"
        : "keine Ruhezeiten";
}
