# N-MailClient (C#/WPF)

> Weiterer Ausbau: [ROADMAP.md](ROADMAP.md)

Hervorgegangen aus einer Proof-of-Concept-Portierung des Mail-Kerns von der
Go+Wails-App nach C#/WPF. Die C#-Fassung ist inzwischen die weitergeführte
Variante; die Go+Wails-Version ist eingestellt und liegt als Snapshot im
Nachbarordner `eol-go` (Historie im Git-Verlauf dieses Repos).

## Voraussetzungen

- **.NET SDK 10** (nur die Runtime reicht nicht): <https://dotnet.microsoft.com/download>
- Windows (WPF)

## Bauen & starten

```powershell
dotnet build NMailClient.slnx
dotnet run   --project NMailClient\NMailClient.csproj
```

## Tests

```powershell
dotnet test NMailClient.Tests\NMailClient.Tests.csproj
```

871 Tests. Zwei Gruppen sind per Trait abtrennbar:

| Filter | Wirkung |
|---|---|
| `--filter "Category!=Network"` | ohne Autodiscover-Tests gegen echte Anbieter (so läuft CI) |
| `--filter "Category!=Integration"` | ohne Zugriff auf den Windows-Anmeldeinformationsverwalter |

Die Integrationstests schreiben ausschließlich Einträge mit eindeutiger GUID im Ziel und
räumen sie über `IDisposable` wieder ab.

Beim ersten Start öffnet sich direkt der Konten-Dialog. Serverfelder werden aus der
E-Mail-Adresse vorbelegt (`imap.<domain>` / `smtp.<domain>`), „Verbindung testen" prüft
den IMAP-Login.

## Was die Anwendung kann

Der Mail-Kern im Überblick; den vollständigen Stand samt Kalender, Kontakten,
PGP, Sieve und mailcow-Anbindung führt die [ROADMAP.md](ROADMAP.md).

| Bereich | Umfang |
|---|---|
| Konten | Mehrere Konten, anlegen/bearbeiten/entfernen, Verbindungstest, **Autodiscover** |
| IMAP | Ordnerbaum (Server-Delimiter, Inbox oben), Ungelesen-Badges, Mailliste mit Paginierung („Mehr laden", 50/Seite), Volltextsuche (IMAP SEARCH, 400 ms debounced) |
| Anzeige | HTML- und Textmails via **WebView2** (Chromium), **externe Bilder blockiert** + „Bilder anzeigen"-Banner, Links öffnen im Systembrowser, Anhänge auflisten und „Speichern unter…" |
| Optik | **Light/Dark-Theme**, umschaltbar zur Laufzeit, folgt per Default der Windows-Einstellung; Auswahl wird gemerkt |
| Aktionen | Gelesen/ungelesen, Stern, Löschen (Move in Papierkorb, Fallback `\Deleted`+Expunge) |
| SMTP | Neue Mail, Antworten / Allen antworten / Weiterleiten (mit Zitat), Cc, Signatur, Anhänge per Dialog oder Drag & Drop |
| UI | Drei-Spalten-Layout, Initialen-Avatare, Listen-Virtualisierung, Tastenkürzel (Entf, `u`, `Strg+N`, `F5`) |

## Architektur

```
Models/      Account, MailSummary, MailBody, FolderNode  (POCOs, INotifyPropertyChanged wo nötig)
Services/    ImapService, SmtpService  (MailKit)  ·  SettingsStore (JSON+DPAPI)
             HtmlSanitizer  ·  ThemeManager
ViewModels/  MainViewModel (gesamter Zustand), RelayCommand, ComposeRequest
Views/       MainWindow, AccountsWindow, ComposeWindow  (XAML + schlankes Code-behind)
Themes/      Light.xaml, Dark.xaml (nur Farben)  ·  Controls.xaml (Control-Styles)
```

**Theming:** `Light.xaml`/`Dark.xaml` enthalten dieselben Ressourcenschlüssel und liegen an
Position 0 der `MergedDictionaries`; `ThemeManager` tauscht nur diesen einen Eintrag aus.
Damit das zur Laufzeit greift, beziehen **alle** Styles ihre Farben per `DynamicResource` —
`StaticResource` würde beim Wechsel nicht aktualisieren.

Zuordnung zur Go-Version:

| Go | C# |
|---|---|
| `internal/mail/autodiscover.go` | `Services/Autodiscover.cs` |
| `internal/mail/imap.go` | `Services/ImapService.cs` |
| `internal/mail/smtp.go` | `Services/SmtpService.cs` |
| `internal/store/store.go` | `Services/SettingsStore.cs` |
| `app.go` (gebundene Methoden) | `ViewModels/MainViewModel.cs` |
| Wails-Frontend (HTML/JS) | `Views/*.xaml` |

Die JSON-Feldnamen in `Account` sind absichtlich identisch zu `internal/store/store.go`,
damit ein `db.json` der Go-App perspektivisch übernommen werden kann.

## Autodiscover

Dieselbe Strategie wie `internal/mail/autodiscover.go`, in dieser Reihenfolge:

1. **Mozilla-Autoconfig** — `autoconfig.<domain>`, `<domain>/.well-known/autoconfig/`,
   Thunderbird-ISPDB. Bewusst nur über HTTPS: Serverangaben aus einem unauthentifizierten
   Kanal könnten Mail über einen fremden Host leiten.
2. **DNS-SRV** (RFC 6186) — `_imaps`/`_imap`, `_submissions`/`_submission`.
3. **Raten, per TLS-Handshake verifiziert** — `imap.`/`smtp.`/`mail.<domain>`.

Unterschied zur Go-Version: dort läuft alles streng seriell, wodurch sich die Timeouts
unerreichbarer Hosts auf über eine Minute summieren. Hier laufen die Abfragen **innerhalb
jeder Stufe parallel**, plus ein Gesamtbudget von 12 s mit Fallback auf `mail.<domain>`.
Gemessen: Anbieter mit Autoconfig 100–450 ms, nicht auflösbare Domain 4 s.

**Auslösung:** automatisch **während des Tippens** (700 ms entprellt), sobald die Adresse
vollständig aussieht — kein Fokuswechsel und kein Klick nötig. Sobald der Nutzer selbst in
ein Serverfeld tippt, hält die Automatik an und überschreibt nichts mehr.

Die automatische Suche nutzt nur Stufe 1 und 2 (Antwort in Millisekunden). **Stufe 3 läuft
nur auf Klick auf „Genauer suchen"** — sie verbindet sich testweise zu Port 993/465 und
kostet bis zu 4 s, was bei jeder Tippause unbrauchbar wäre. Bleiben alle Stufen ergebnislos,
werden `mail.<domain>` als Vorschlag eingetragen und über `ProbeResult.IsVerified`
ausdrücklich als *ungeprüft* gekennzeichnet. Ergebnisse werden pro Domain gecacht.

## Bewusste Vereinfachungen

- **Ein `ImapClient` pro Konto**, alle Zugriffe über ein `SemaphoreSlim` serialisiert
  (MailKit-Clients sind nicht threadsicher). Produktiv wäre ein Verbindungspool sinnvoll.
- **Abgelehnte Anmeldedaten werden gemerkt** und nicht erneut versucht. Ohne diese Sperre
  löst bei falschem Passwort jede Folgeoperation — Ordnerbaum, `STATUS` je Ordner, jede
  Aktion — einen weiteren Login-Versuch aus; das ergibt dutzende Fehlversuche und damit
  eine fail2ban-Sperre. Aufgehoben wird sie durch neue Zugangsdaten.
  Für SMTP fehlt das Gegenstück noch (dort entsteht pro Sendeversuch nur ein Login).
- **Passwörter** liegen im **Windows-Anmeldeinformationsverwalter**, nicht in `db.json`
  (Ziel `NMailClient:mail:<accountId>`). Die frühere DPAPI-Variante wird beim ersten
  Start automatisch übernommen und aus der Datei entfernt.
- **`HtmlSanitizer` ist regex-basiert.** Hier gehört ein echter Parser hin (AngleSharp):
  reguläre Ausdrücke passen auf wohlgeformtes Markup, und genau das schreibt ein
  Angreifer nicht. Bis dahin trägt die zweite Verteidigungslinie — die Anzeige läuft
  in einer WebView2 ohne Skriptausführung. **Offener Punkt.**
- **WebView2 braucht die Evergreen-Runtime.** Auf Windows 11 ist sie vorinstalliert; fehlt
  sie, bleibt die Mailanzeige leer und die Statusleiste meldet das — die App läuft weiter.
- **Dark Mode bei HTML-Mails ist eine Näherung.** Mails bringen eigene harte Farben mit;
  `HtmlSanitizer` neutralisiert Hintergründe und Schwarztöne per CSS, ein perfektes Ergebnis
  ist bei fremdem Markup nicht erreichbar.
- **Ein Semaphor je Konto** bedeutet, dass eine laufende Serversuche kurzzeitig auch
  andere Aktionen desselben Kontos aufhält. IMAP IDLE hat deshalb bereits eine eigene
  Verbindung; für die übrige Hintergrundlast steht das noch aus.
