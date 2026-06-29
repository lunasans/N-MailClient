# N-MailClient — Go + Wails

Datenschutzorientierter Desktop-E-Mail-Client für Windows, gebaut mit **Go + Wails v2**
und einer schlanken Vanilla-JS-Oberfläche (kein Node-Build nötig). Neufassung der
früheren Electron-Version (die liegt im Branch `electron-legacy`).

## Funktionen

- **Mail**: IMAP/SMTP (implizites TLS 993/465, STARTTLS 143/587), Ordnerbaum, Lesen
  (Text/HTML, sandboxed iframe), Senden, Antworten/Weiterleiten, Anhänge, Entwürfe
  (Auto-Save), Volltextsuche, Etiketten, Threads, gemeinsamer Posteingang, Paginierung
- **Sicherheit**: SPF/DKIM/DMARC-Anzeige + Spoofing-Warnung, DANE/TLSA-Prüfung,
  MTA-STS, externe Bilder blockiert, PGP (Ver-/Entschlüsseln, Key-Manager),
  Passwörter im **OS-Keychain**
- **Produktivität**: Inbox-Kategorien (Tabs), Drag & Drop in Ordner, Rechtschreibprüfung,
  Übersetzung (LibreTranslate), Anhang-Archiv, Druck, Tastenkürzel, Undo-Toast,
  Offline-Cache (Listen + Inhalte)
- **Groupware**: Kalender (CalDAV), Kontakte (CardDAV, inkl. vCard-Im/Export),
  Sieve-Filter (ManageSieve, Regelassistent, Abwesenheitsnotiz)
- **Komfort**: robustes Autodiscover (Autoconfig/ISPDB + DNS-SRV), Dark Mode,
  Systembenachrichtigungen, Tray-Icon, Autostart

## Voraussetzungen (Build)

- **Go** (Version siehe `go.mod`) — https://go.dev/dl/
- **Wails CLI**: `go install github.com/wailsapp/wails/v2/cmd/wails@latest`
- **NSIS** (nur für den Installer): https://nsis.sourceforge.io/ — `makensis` muss im PATH sein
- **WebView2 Runtime** (auf Windows 10/11 i. d. R. vorinstalliert)

Ein C-Compiler ist nicht nötig (neuer Go-WebView2Loader).

## Bauen & Starten

```powershell
wails dev                                   # Live-Entwicklung
wails build -platform windows/amd64         # build/bin/n-mailclient-go.exe
wails build -platform windows/amd64 -nsis   # zusätzlich den Installer
```

Releases werden bei jedem `v*`-Tag automatisch von GitHub Actions gebaut
(siehe `.github/workflows/release.yml`).

## Architektur

```
main.go            App-Bootstrap, Wails-Window, Bind(App)
app.go             gebundene Methoden (Frontend ruft window.go.main.App.*)
internal/store/    Konten-Store (JSON; Passwörter im OS-Keychain)
internal/mail/     IMAP, SMTP, Autodiscover, DANE, Offline-Cache
internal/{calendar,contacts,sieve,translate}/  Groupware-Dienste
pgp.go · mta_sts.go · tray_windows.go · dwm_windows.go
frontend/dist/     Vanilla-JS-UI (index.html)
```

UI ↔ gebundene Go-Methoden (statt IPC), Mail-Logik nur im Go-Backend.

## Bekannte Einschränkungen

- **Windows-only**: Autostart (Registry), Tray, DANE-System-DNS und Toasts nutzen
  Windows-APIs; ein Linux-Build (.deb) ist als Cross-Platform-Port geplant (Roadmap 1.0.0).
- **Unsigniert**: Der Installer trägt keine Code-Signatur → SmartScreen-Warnung.
- Anhänge offline nur eingeschränkt (Inhalte gecacht, Datei-Download braucht Verbindung).

Details und Roadmap: siehe [STATUS.md](STATUS.md).
