using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NMailClient.Poc.Services.Shell;

/// <summary>
/// Symbol im Infobereich, direkt über <c>Shell_NotifyIcon</c>.
///
/// Kein Fremdpaket: die Windows-Forms-Variante (<c>NotifyIcon</c>) zöge die
/// gesamte WinForms-Schicht in eine WPF-Anwendung, und die gängigen Bibliotheken
/// dafür sind nur dünne Hüllen um genau diese API.
///
/// Die Sprechblase (<c>NIF_INFO</c>) erledigt nebenbei die Benachrichtigung bei
/// neuer Post — dafür wäre sonst <c>CommunityToolkit.WinUI.Notifications</c>
/// nötig gewesen, was das Ziel-Framework auf <c>windows10.0.17763</c> gehoben
/// hätte.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 1;

    private const int WmLButtonDblClk = 0x0203;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int NinBalloonUserClick = WmApp + 5;

    private HwndSource? _window;
    private bool _added;
    private uint _id;

    /// <summary>Doppelklick auf das Symbol oder Klick auf die Sprechblase.</summary>
    public event EventHandler? Activated;

    /// <summary>Rechtsklick – der Aufrufer zeigt sein Menü.</summary>
    public event EventHandler? MenuRequested;

    public bool IsVisible => _added;

    public void Show(string tooltip)
    {
        if (_added) return;

        _window ??= CreateMessageWindow();
        _id = 1;

        var data = Describe(tooltip);
        data.uFlags = NifMessage | NifIcon | NifTip;

        if (Shell_NotifyIcon(NimAdd, ref data))
        {
            _added = true;

            // Version 4 liefert die Mausposition mit und macht die Sprechblasen-
            // Ereignisse überhaupt erst zuverlässig.
            var version = Describe(tooltip);
            version.uTimeoutOrVersion = 4;
            Shell_NotifyIcon(NimSetVersion, ref version);
        }
        else
        {
            AppLog.Warn("Symbol im Infobereich konnte nicht angelegt werden.");
        }
    }

    public void Hide()
    {
        if (!_added) return;

        var data = Describe("");
        data.uFlags = 0;
        Shell_NotifyIcon(NimDelete, ref data);
        _added = false;
    }

    public void SetTooltip(string tooltip)
    {
        if (!_added) return;

        var data = Describe(tooltip);
        data.uFlags = NifTip;
        Shell_NotifyIcon(NimModify, ref data);
    }

    /// <summary>
    /// Sprechblase anzeigen. Windows entscheidet selbst, ob sie erscheint — im
    /// Benachrichtigungsassistenten unterdrückte Meldungen kommen still ins
    /// Info-Center. Ruhezeiten prüft deshalb der Aufrufer, nicht diese Klasse.
    /// </summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;

        var data = Describe("");
        data.uFlags = NifInfo;
        data.szInfoTitle = Trim(title, 63);
        data.szInfo = Trim(text, 255);
        data.dwInfoFlags = 0x01;      // NIIF_INFO – das Standardsymbol

        Shell_NotifyIcon(NimModify, ref data);
    }

    // ---- Fenster für die Rückmeldungen -------------------------------------

    private HwndSource CreateMessageWindow()
    {
        // Ein unsichtbares Fenster; Shell_NotifyIcon braucht ein Ziel für seine
        // Nachrichten.
        var source = new HwndSource(new HwndSourceParameters("NMailClient.Poc.Tray")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
        });

        source.AddHook(WndProc);
        return source;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != CallbackMessage) return IntPtr.Zero;

        // Ab Version 4 steckt das Ereignis im unteren Wort von lParam.
        var eventCode = (int)(lParam.ToInt64() & 0xFFFF);

        switch (eventCode)
        {
            case WmLButtonDblClk:
            case NinBalloonUserClick:
                Activated?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case WmRButtonUp:
                // Ohne das bleibt ein aufgeklapptes Menü stehen, wenn der
                // Anwender daneben klickt.
                SetForegroundWindow(hwnd);
                MenuRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private NotifyIconData Describe(string tooltip) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window?.Handle ?? IntPtr.Zero,
        uID = _id,
        uCallbackMessage = CallbackMessage,
        hIcon = AppIcon(),
        szTip = Trim(tooltip, 127),
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Trim(string text, int max)
        => text.Length <= max ? text : text[..max];

    private static IntPtr _icon;

    /// <summary>
    /// Das Symbol der Anwendung aus der eigenen EXE. Fällt auf das
    /// Standardsymbol zurück, damit ein fehlendes Icon kein leeres Feld erzeugt.
    /// </summary>
    private static IntPtr AppIcon()
    {
        if (_icon != IntPtr.Zero) return _icon;

        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path))
            _icon = ExtractIcon(IntPtr.Zero, path, 0);

        if (_icon == IntPtr.Zero || _icon == new IntPtr(1))
            _icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));      // IDI_APPLICATION

        return _icon;
    }

    public void Dispose()
    {
        Hide();
        _window?.Dispose();
        _window = null;
    }

    // ---- Win32 -------------------------------------------------------------

    private const int NimAdd = 0x00;
    private const int NimModify = 0x01;
    private const int NimDelete = 0x02;
    private const int NimSetVersion = 0x04;

    private const int NifMessage = 0x01;
    private const int NifIcon = 0x02;
    private const int NifTip = 0x04;
    private const int NifInfo = 0x10;

    /// <summary>
    /// Bewusst <c>DllImport</c> und nicht <c>LibraryImport</c>: der Quelltext-
    /// generator kann Strukturen mit Zeichenkettenfeldern nicht behandeln
    /// (SYSLIB1051) und verlangte ausserdem unsicheren Code.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string file, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
