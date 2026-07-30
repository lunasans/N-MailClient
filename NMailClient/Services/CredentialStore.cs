using System.Runtime.InteropServices;
using System.Text;

namespace NMailClient.Services;

/// <summary>
/// Passwörter im Windows-Anmeldeinformationsverwalter (Credential Manager).
/// Pendant zum OS-Keychain der Go-Version; ersetzt die DPAPI-Abkuerzung der fruehen Fassung.
///
/// Absichtlich per P/Invoke auf advapi32 statt über WinRT
/// (<c>Windows.Security.Credentials</c>): das hätte ein Ziel-Framework wie
/// <c>net10.0-windows10.0.19041.0</c> erzwungen. Hier genügt <c>net10.0-windows</c>.
/// </summary>
public static partial class CredentialStore
{
    private const string Prefix = AppPaths.Name;

    /// <summary>Ein Eintrag je Konto und Zweck, z.B. "NMailClient:mail:{id}".</summary>
    public static string TargetFor(string accountId, string purpose = "mail")
        => $"{Prefix}:{purpose}:{accountId}";

    /// <summary>Kontounabhängige Geheimnisse, z.B. der Übersetzungs-API-Schlüssel.</summary>
    public static string GlobalTarget(string purpose) => $"{Prefix}:{purpose}";

    // ---- Win32 -------------------------------------------------------------

    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    // Bewusst DllImport statt LibraryImport: der quellcodegenerierte Marshaller
    // unterstützt Strukturen mit String-Feldern nicht (SYSLIB1051) und verlangt
    // außerdem AllowUnsafeBlocks. Für vier Aufrufe ist das der falsche Preis.

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags,
        out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(string? filter, uint flags, out uint count,
        out IntPtr credentials);

    // ---- API ---------------------------------------------------------------

    /// <summary>Speichert oder ersetzt ein Passwort. Leerer Wert löscht den Eintrag.</summary>
    public static bool Save(string target, string userName, string secret)
    {
        if (string.IsNullOrEmpty(secret)) return Delete(target);

        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);

            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = string.IsNullOrEmpty(userName) ? target : userName,
            };
            return CredWrite(ref cred, 0);
        }
        finally { Marshal.FreeCoTaskMem(blob); }
    }

    /// <summary>Liest ein Passwort; null, wenn kein Eintrag existiert.</summary>
    public static string? Read(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr))
        {
            // Fehlender Eintrag ist der Normalfall, kein Fehler.
            var err = Marshal.GetLastWin32Error();
            if (err != ERROR_NOT_FOUND) Log(target, "CredRead", err);
            return null;
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return null;

            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally { CredFree(ptr); }
    }

    public static bool Delete(string target)
    {
        if (CredDelete(target, CRED_TYPE_GENERIC, 0)) return true;
        var err = Marshal.GetLastWin32Error();
        if (err == ERROR_NOT_FOUND) return true; // schon weg – Ziel erreicht
        Log(target, "CredDelete", err);
        return false;
    }

    /// <summary>
    /// Übernimmt alle Einträge, die noch unter <paramref name="legacyPrefix"/>
    /// liegen, auf das heutige Präfix und entfernt die alten.
    ///
    /// Enumeriert statt gezielt zu lesen: die Zwecke ("mail", "mailcow",
    /// "translate", PGP-Mantras …) stehen über den ganzen Quelltext verteilt.
    /// Eine Liste davon hier zu pflegen hiesse, beim nächsten neuen Zweck ein
    /// Passwort stillschweigend zurückzulassen.
    /// </summary>
    /// <returns>Anzahl übernommener Einträge.</returns>
    public static int MigrateLegacyPrefix(string legacyPrefix)
    {
        if (!CredEnumerate($"{legacyPrefix}:*", 0, out var count, out var array))
        {
            var err = Marshal.GetLastWin32Error();
            // Nichts gefunden ist der Normalfall – jeder Start nach dem ersten.
            if (err != ERROR_NOT_FOUND) Log($"{legacyPrefix}:*", "CredEnumerate", err);
            return 0;
        }

        var alt = new List<string>((int)count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var ptr = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
                if (!string.IsNullOrEmpty(cred.TargetName)) alt.Add(cred.TargetName);
            }
        }
        finally { CredFree(array); }

        var migrated = 0;
        foreach (var oldTarget in alt)
        {
            var newTarget = Prefix + oldTarget[legacyPrefix.Length..];

            // Ein bereits vorhandener neuer Eintrag ist der jüngere – nicht
            // überschreiben, nur den alten wegräumen.
            if (Read(newTarget) is null)
            {
                var secret = Read(oldTarget);
                if (secret is null) continue;
                if (!Save(newTarget, "", secret)) continue;
                migrated++;
            }
            Delete(oldTarget);
        }

        if (migrated > 0)
            AppLog.Info($"{migrated} Anmeldeinformation(en) von '{legacyPrefix}' "
                        + $"auf '{Prefix}' übernommen.");

        return migrated;
    }

    private static void Log(string target, string call, int error)
        => AppLog.Warn($"{call} für '{target}' fehlgeschlagen (Win32 {error}).");
}
