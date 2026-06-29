# N-MailClient — Go + Wails (Proof of Concept)

Test-Implementierung des **MVP-Kerns** in **Go + Wails** als Stack-Evaluierung —
**nicht** die vollständige 0.1.0-Featureliste. Enthalten:

- Konten anlegen/auflisten (JSON-Store im User-Config-Verzeichnis)
- IMAP: Ordner auflisten, Posteingang/Folder lesen (Liste), Nachricht öffnen
  (Text/HTML) — via `emersion/go-imap` + `go-message`
- SMTP: einfache Textmail senden (`net/smtp`, implizites TLS 465 / STARTTLS 587)
- Schlanke **Vanilla-JS-UI** (kein Node-Build nötig)

> **Status:** Auf dem Build-Rechner war **keine Go-Toolchain installiert**, daher ist
> dieser Code **nicht kompiliert/getestet** — es ist ein sauberes Gerüst. Kleinere
> Anpassungen beim ersten Build sind möglich.

## Voraussetzungen

- **Go** ≥ 1.22 — https://go.dev/dl/
- **C-Compiler** (Wails braucht cgo): unter Windows z. B. MSYS2/MinGW oder TDM-GCC
- **WebView2 Runtime** (auf Windows 11 i. d. R. vorinstalliert)
- **Wails-CLI:**
  ```powershell
  go install github.com/wailsapp/wails/v2/cmd/wails@latest
  wails doctor   # prüft die Umgebung
  ```

## Starten

```powershell
cd experiments/go-wails
go mod tidy        # Abhängigkeiten auflösen
wails dev          # Live-Entwicklung (Fenster öffnet sich)
# oder:
wails build        # erzeugt build/bin/n-mailclient-go.exe
```

## Architektur (analog zur Electron-Version)

```
main.go            App-Bootstrap, Wails-Window, Bind(App)
app.go             gebundene Methoden (Frontend ruft window.go.main.App.*)
internal/store/    Konten-Store (JSON)
internal/mail/     imap.go (lesen) · smtp.go (senden)
frontend/dist/     Vanilla-JS-UI (index.html)
```

Die Trennung ist identisch zum Electron-Vorbild: **UI ↔ gebundene Go-Methoden**
(statt IPC), Mail-Logik nur im Go-Backend.

## Bewusste PoC-Grenzen

- **Passwort im Klartext** im JSON-Store. Produktiv: OS-Keychain
  (`github.com/zalando/go-keyring`) — Pendant zu Electrons `safeStorage`.
- Nur **IMAPS (993)** + SMTP **465/587**, **eine** Empfängeradresse, **Plaintext**-Versand.
- **Kein** Autodiscover, Kalender (CalDAV), Kontakte (CardDAV), Sieve, Anhänge,
  Suche, Etiketten, PGP — die decken die emersion-Libraries (`go-webdav`,
  `go-vcard`, `go-ical`, `go-sieve`, ProtonMail `gopenpgp`) aber sauber ab und
  wären die nächsten Schritte.

## Fazit der Evaluierung

Der Stack trägt: Das emersion-Ökosystem deckt IMAP/SMTP/MIME (und perspektivisch
CalDAV/CardDAV/Sieve/PGP) kohärent ab, Wails behält die Web-UI bei und liefert
deutlich kleinere Binaries als Electron. Hauptaufwand eines echten Umstiegs wäre
die Portierung der Backend-Services von TypeScript nach Go — die UI bliebe nahezu.
