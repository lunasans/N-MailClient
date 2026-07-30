using System.Runtime.InteropServices;
using System.Text;

namespace NMailClient.Poc.Services;

/// <summary>
/// Passwörter im Windows-Anmeldeinformationsverwalter (Credential Manager).
/// Pendant zum OS-Keychain der Go-Version; ersetzt die DPAPI-Abkürzung der PoC.
///
/// Absichtlich per P/Invoke auf advapi32 statt über WinRT
/// (<c>Windows.Security.Credentials</c>): das hätte ein Ziel-Framework wie
/// <c>net10.0-windows10.0.19041.0</c> erzwungen. Hier genügt <c>net10.0-windows</c>.
/// </summary>
public static partial class CredentialStore
{
    private const string Prefix = "NMailClient.Poc";

    /// <summary>Ein Eintrag je Konto und Zweck, z.B. "NMailClient.Poc:mail:{id}".</summary>
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

    private static void Log(string target, string call, int error)
        => AppLog.Warn($"{call} für '{target}' fehlgeschlagen (Win32 {error}).");
}
