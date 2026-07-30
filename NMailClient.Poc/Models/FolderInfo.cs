namespace NMailClient.Poc.Models;

/// <summary>
/// Ein Ordner, wie ihn der Server meldet – ohne MailKit-Typen, damit der
/// Baumaufbau testbar bleibt.
/// </summary>
/// <param name="Delimiter">
/// Server-Trennzeichen der Hierarchie ('/' oder '.'); '\0' bedeutet flach.
/// </param>
public record FolderInfo(
    string FullName,
    string Name,
    char Delimiter,
    int Unread,
    bool IsSelectable);
