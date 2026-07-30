namespace NMailClient.Poc.Models;

/// <summary>
/// Ein geladener Entwurf, aufbereitet für den Composer.
/// </summary>
/// <param name="AttachmentPaths">
/// Anhänge werden in einen temporären Ordner ausgepackt, weil der Composer mit
/// Dateipfaden arbeitet. Sie werden beim erneuten Speichern wieder eingebettet.
/// </param>
public record DraftContent(
    uint Uid,
    string To,
    string Cc,
    string Subject,
    string Body,
    string? InReplyTo,
    IReadOnlyList<string> References,
    IReadOnlyList<string> AttachmentPaths);
