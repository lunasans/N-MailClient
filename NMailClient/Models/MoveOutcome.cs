namespace NMailClient.Models;

/// <summary>
/// Ergebnis einer verschiebenden Operation (Verschieben, Archivieren, Löschen).
///
/// <see cref="NewUids"/> sind die UIDs im Zielordner. Sie liefert der Server nur mit
/// der UIDPLUS-Erweiterung; ohne sie bleibt die Liste leer. Genau daran hängt, ob
/// „Rückgängig" angeboten werden darf – ohne Ziel-UIDs liesse sich nicht sagen,
/// welche Nachrichten zurückzuholen wären.
/// </summary>
public record MoveOutcome(string? TargetFolder, IReadOnlyList<uint> NewUids)
{
    /// <summary>Nichts verschoben – etwa bei endgültigem Löschen per Expunge.</summary>
    public static readonly MoveOutcome None = new(null, []);

    public bool CanUndo => TargetFolder is not null && NewUids.Count > 0;
}
