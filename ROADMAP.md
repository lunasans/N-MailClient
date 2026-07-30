# Roadmap — N-MailClient (C#/WPF)

Zielbild: Funktionsgleichstand mit der Go+Wails-Version (66 Features aus
[STATUS.md](../STATUS.md) plus 0.6.0/0.7.0), aber nach **technischen Abhängigkeiten**
geordnet statt nach der Reihenfolge, in der die Go-Version historisch gewachsen ist.

Aufwandsskala: **S** < 1 Tag · **M** 1–3 Tage · **L** 3–8 Tage · **XL** > 8 Tage.
Schätzungen sind grob und gehen von einer Person aus.

## Getroffene Entscheidungen

| Frage | Entscheidung | Folge |
|---|---|---|
| Linux-Port? | **nein** | WPF bleibt, kein Avalonia. WebView2 ist damit unbedenklich, ebenso Win32-P/Invoke für Tray/Autostart. |
| CalDAV/CardDAV | **Fremdpakete, wo reif** | `iCal.Net` + `FolkerKinzel.VCards` + `WebDav.Client`; die CalDAV-/CardDAV-Protokollschicht selbst schreiben. |
| Lokaler Cache | **SQLite** | `Microsoft.Data.Sqlite` + `Dapper`. Trägt Offline-Modus, Suche (FTS5) und Threading gemeinsam. |
| Composer Rich Text | **WPF `RichTextBox`** | Mailanzeige bleibt WebView2. `FlowDocument`→HTML wird selbst geschrieben — siehe 0.4.0. |

### Paketstand (jeweils neuestes Stable)

| Paket | Version | Zweck |
|---|---|---|
| `MailKit` / `MimeKit` | 4.17.0 | IMAP/SMTP, MIME, OpenPGP |
| `Microsoft.Web.WebView2` | 1.0.4078.44 | Mailanzeige, ggf. Composer |
| `DnsClient` | 1.8.0 | SRV fuer Autodiscover. **Nicht** fuer TLSA — dafuer gibt es die rohen RDATA-Bytes nicht heraus, siehe eigene DNSSEC-Schicht in 0.6.0 |
| `Microsoft.Data.Sqlite` | 10.0.10 | Cache, Suche |
| `Dapper` | 2.1.79 | schlanker Datenzugriff |
| `SQLitePCLRaw.bundle_e_sqlite3` | 3.0.5 | **ausdruecklich vorgezogen** — `Microsoft.Data.Sqlite` zieht sonst 2.1.11 nach, dessen native Bibliothek eine Luecke hohen Schweregrads hat (GHSA-2m69-gcr7-jv3q) |
| `iCal.Net` | 5.2.3 | iCalendar lesen/schreiben |
| `FolkerKinzel.VCards` | 8.2.0 | vCard 2.1–4.0 |
| ~~`WebDav.Client`~~ | — | **nicht verwendet** — kennt kein REPORT; die DAV-Schicht ist selbst geschrieben (`Services/Dav/`) |

---

## Ausgangsstand der Portierung

| Bereich | Umfang |
|---|---|
| Konten | mehrere Konten, CRUD, DPAPI-Passwörter, Verbindungstest, Autodiscover (Autoconfig/SRV/Probe) |
| IMAP | Ordnerbaum + Ungelesen-Zähler, Mailliste mit Paginierung, Volltextsuche, Body (HTML/Text), Anhänge speichern, gelesen/Stern/löschen |
| SMTP | Senden, Antworten/Allen/Weiterleiten, Cc, Signatur, Anhänge (Dialog + Drag & Drop) |
| UI | 3-Spalten-Layout, Light/Dark, WebView2-Anzeige, Bildblocker, Avatare, Virtualisierung, Tastenkürzel |

Damit sind grob **12 der 66** Features abgedeckt — der Mail-Kern, nicht die Breite.

---

## 0.2.0 — Fundament (vor allen Features)

Diese Punkte sind keine Features, aber alles Weitere hängt daran. Wird das übersprungen,
werden die späteren Meilensteine teurer.

| Punkt | Aufwand | Status | Warum zuerst |
|---|---|---|---|
| **SMTP-Auth-Sperre** analog IMAP | S | **erledigt** | Bei falschem Passwort sonst pro Sendeversuch ein Fehlversuch. Wirkt nur, weil `SmtpService` jetzt pro Konto wiederverwendet wird. |
| **Keychain statt DPAPI** | S | **erledigt** | `CredentialStore` auf Windows Credential Manager (`advapi32`, `DllImport`). DPAPI-Altbestände werden beim ersten Start übernommen, Passwörter aus `db.json` entfernt. |
| **Einstellungs-Modell** (globale Optionen, nicht nur Konten) | M | **erledigt** | `AppSettings` in `db.json` unter `settings`, mit `schema`-Feld für künftige Migrationen. `theme.txt` wurde eingezogen und gelöscht. |
| **Logging in Datei** statt MessageBox | S | **erledigt** | `AppLog` nach `%LOCALAPPDATA%\NMailClient\app.log`, 1 MiB Rotation. Bei DI-Einführung hinter `ILogger` ziehen. |
| **Zentraler Dialog „Einstellungen"** | M | **erledigt** | `SettingsWindow` mit Reitern Allgemein/Konten; der frühere `AccountsWindow` ist jetzt `AccountsView` (UserControl) und darin eingebettet. Sieve/Kalender kommen als weitere Reiter. |
| **DI** (`Microsoft.Extensions.DependencyInjection` 10.0.10) | M | **erledigt** | Container in `App`; `MailServiceRegistry` aus dem ViewModel gelöst, Dialoge über Fabriken. Registrierungen sind getestet. |
| **Unit-Tests** | M | **erledigt** | Projekt `NMailClient.Tests` (xunit.v3), **615 Faelle offline** in 34 Dateien plus **18 Netz-Tests**, die per Trait abgetrennt sind und in der CI nicht laufen. Schwerpunkte: DNSSEC-Kette und DANE (`DnssecCryptoTests` mit dem Root-KSK als Pruefvektor, `DnsWireTests` mit dem Sortierbeispiel aus RFC 4034, `DnssecChainTests` und `RecipientSecurityNetworkTests` gegen echte Domains), OpenPGP (`PgpTests`, voller Durchstich mit erzeugten Schluesseln), Cache mit UIDVALIDITY-Wache und FTS5 (`MailCacheTests`), Transportsicherheit (`TransportSecurityTests`), sowie `FlowDocumentHtml`, Threading, Kategorisierung, Kalender, CardDAV, Aktualisierung und die Fensterkonstruktion. |
| **Build/CI** (`dotnet publish` single-file, GitHub Actions) | M | **erledigt** | `.github/workflows/build.yml`. Netzwerktests in CI ausgeschlossen. Ergebnis: 8,2 MB EXE (framework-abhängig). |

---

## 0.3.0 — Mail-Kern vervollständigen

Was ein Mailclient im Alltag braucht und heute fehlt.

| Feature | Aufwand | Status | Anmerkung |
|---|---|---|---|
| Ordner anlegen/löschen/umbenennen + Reihenfolge | M | **erledigt** | Kontextmenü am Ordnerbaum. Posteingang geschützt, Löschen nur ohne Unterordner, Delimiter im Namen abgewiesen. Reihenfolge per „Nach oben/unten", je Konto in `folderOrder` gespeichert; nicht gelistete Ordner folgen alphabetisch. |
| Mail in Ordner verschieben + Archivieren | S | **erledigt** | „Verschieben nach" mit flacher, eingerückter Ordnerliste; Archiv über `\Archive`-Attribut mit Rückfall auf „Archive"/„Archiv". |
| Mehrfachauswahl (Strg+A, Strg+Klick) + Batch-Aktionen | M | **erledigt** | `SelectionMode="Extended"`; alle Aktionen senden **ein** IMAP-Kommando für die gesamte Auswahl. |
| Bestätigungsabfrage beim Löschen (konfigurierbar) | S | **erledigt** | `AppSettings.ConfirmBeforeDelete`, Standard an. |
| Rückgängig-Toast für Löschen/Archivieren/Verschieben | M | **erledigt** | Streifen über der Liste, 10 s sichtbar. Wird **nur** angeboten, wenn der Server Ziel-UIDs meldet (UIDPLUS) — sonst wäre nicht bestimmbar, was zurückzuholen ist. |
| Entwürfe mit Auto-Save (10 s) | M | **erledigt** — `APPEND` mit `\Draft\Seen` in den Drafts-Ordner (SpecialFolder + Fallback „Drafts"/„Entwürfe"), Vorgänger wird ersetzt (braucht UIDPLUS), letzter Stand beim Schließen, Entwurf wird nach Versand entfernt. Wieder öffnen per Doppelklick oder Kontextmenü im Entwürfe-Ordner (Anhänge werden in einen temporären Ordner ausgepackt, Signatur nicht doppelt angehängt). |
| Mail-Quelltext-Ansicht, Drucken | S | **erledigt** — Quelltext als eigenes Fenster (monospace, Kopieren); Drucken über den Chromium-Druckdialog von WebView2 |
| Datumsgruppierung + Listendichte | M | **erledigt** — Gruppen Heute/Gestern/Diese Woche/Letzte Woche/Diesen Monat/Monatsname; Dichte Kompakt/Normal/Komfortabel, beides in den Einstellungen |
| Hover-Schnellaktionen in der Liste | S | **erledigt** — Archiv/Löschen erscheinen beim Überfahren anstelle von Datum/Symbolen und wirken **nur auf diese Zeile**, nicht auf die Auswahl |
| Filter „nur unbeantwortete" | S | **erledigt** — Umschalter in der Toolbar (`SEARCH NOT ANSWERED`), kombinierbar mit der Textsuche. Smart-/virtuelle Ordner bewusst **gestrichen** — haben sich schon in der Go-Version nicht bewährt. |
| Etiketten (IMAP-Keywords) + Manager | M | **erledigt** — farbige Chips in der Liste, Zuweisung über Kontextmenü (batchfähig), Verwaltung als dritter Reiter der Einstellungen. Keyword wird aus dem Anzeigenamen abgeleitet (ASCII-Atom) und bleibt beim Umbenennen stabil. |
| Spam-/Ham-Training (Junk-Move + „Kein Spam") | S | **erledigt** — Kontextmenü: „Spam" verschiebt in den Junk-Ordner (trainiert bei mailcow rspamd), „Kein Spam" nur im Junk-Ordner aktiv, legt zurück in den Posteingang. Beides mit Rückgängig. |
| **IMAP IDLE** | L | **erledigt** — `ImapIdleService` mit eigener Verbindung je Konto (RFC 2177, 9-min-Erneuerung, Backoff bei Abbrüchen, NOOP-Polling-Fallback für Server ohne IDLE, Stopp bei abgelehnter Anmeldung). Meldung in-App: Statuszeile + Badge + Nachladen des offenen Posteingangs |
| Windows-Toast + Sound bei neuer Mail | M | **erledigt, anders geloest** — die Entscheidung Toolkit-Paket vs. COM-Interop hat sich erübrigt: die Sprechblase des Infobereich-Symbols (`NIF_INFO`) meldet neue Post, ohne das Ziel-Framework auf `windows10.0.17763` zu heben. Windows leitet unterdrückte Meldungen selbst ins Info-Center. Kein eigener Ton — den Systemklang zu übersteuern wäre aufdringlich |
| Konversations-/Thread-Ansicht | L | **erledigt** — `ThreadBuilder` gruppiert per Union-Find über `Message-ID`/`In-Reply-To`/`References` (RFC 5322), **nicht** über Betreffe wie die Go-Version. Betreff nur als Rückfall für Nachrichten ganz ohne Header. Umschalter in der Toolbar, Anzahl-Badge je Verlauf. |

---

## 0.4.0 — Composer & Produktivität

| Feature | Aufwand | Anmerkung |
|---|---|---|
| **Rich-Text-Composer** (`RichTextBox` + Formatierleiste) | L | **erledigt** — Fett/Kursiv/Unterstrichen/Durchgestrichen, Listen, Nummerierung, Zitat, Link, Schriftgröße, Formatierung löschen |
| **`FlowDocument`→HTML-Konverter** | M | **erledigt** — `FlowDocumentHtml`, 22 Tests je Konstrukt; Versand als multipart/alternative (HTML + Textfassung) |
| Rechtschreibprüfung DE/EN/Auto | S | **teilweise** — `SpellCheck.IsEnabled` aktiv, Sprache fest `de-DE`. Umschalter in den Einstellungen fehlt; **nur Sprachen, die in Windows installiert sind** |
| Vorlagen/Textbausteine | S | **erledigt** — Auswahl fügt an der Einfügemarke ein, „Als Baustein" speichert Auswahl oder ganzen Text; liegt in `db.json` |
| Mit Alias absenden + Signatur pro Alias | M | **erledigt** — „Von"-Auswahl im Composer; Signatur wechselt beim Absenderwechsel mit (ersetzt nur die automatisch eingefügte). Verwaltung im Konten-Reiter (Adresse, Anzeigename, eigene Signatur) mit Prüfung auf Vollständigkeit und Dubletten |
| Optionales BCC, Sendeverfolgung (DSN) | S | **erledigt** — Bcc-Feld per Umschalter, `Disposition-Notification-To` zeigt auf den tatsächlichen Absender (nicht aufs Konto) |
| Mail-Versand rückgängig (Timer) + geplanter Versand | M | **erledigt** — `OutboxService` mit persistenter Warteschlange (`.eml` + `queue.json`), Frist einstellbar (Standard 10 s, 0 = sofort), „Später…" mit Schnellauswahl, Nachholen beim Start, Wiederholung mit Abstand bei Fehlern. Wie in Go: **nur während die App läuft** |
| Snooze + Follow-up/Wiedervorlage | M | **erledigt** — `ReminderStore` (eigene `reminders.json`); Snooze blendet aus bis zum Zeitpunkt, Wiedervorlage lässt sichtbar und markiert die Zeile. Vorgaben 1 h / heute Abend / morgen früh / 1 Woche. Rein lokal — IMAP kennt kein Snooze |
| ~~Empfänger-Autovervollständigung~~ | S | **nach 0.6.0 verschoben** — setzt die Kontakte aus 0.5.0 voraus |
| **Anhang-Archiv** (lokal, nach Absender/Jahr/Monat) + Browse-Ansicht | L | **erledigt** (vorgezogen aus 0.6.0) — Ablage einzeln oder alle auf einmal; Browse-Fenster mit Suche, Öffnen, Löschen, „Ordner öffnen"; Ablageordner frei wählbar. Pfadbildung gegen Ausbruch abgesichert (`..`, Pfadtrenner, Windows-Gerätenamen), keine Datei wird überschrieben |
| Inbox-Kategorien/Tabs | M | **erledigt** — Reiter Allgemein/Newsletter/Werbung/Soziales (abschaltbar, Standard aus). Heuristik über `List-Unsubscribe`, `Precedence`, `Auto-Submitted`, noreply-Absender und Social-Domains; Kategorie je Absender dauerhaft überschreibbar. Im Zweifel „Allgemein" |
| Übersetzung (LibreTranslate, Text + HTML) | M | **erledigt** — Knopf in der Vorschau, `format=html` erhält das Markup; Umschalten zwischen Übersetzung und Original ohne erneute Anfrage. Adresse in den Einstellungen (leer = aus), Schlüssel im Anmeldeinformationsverwalter |

### Der `FlowDocument`→HTML-Konverter

Entschieden: Composer auf WPF-`RichTextBox`, Mailanzeige weiterhin WebView2. Heute ist der
Composer eine reine `TextBox`, Rich Text existiert noch nicht.

Der Konverter ist die bekannte Schwachstelle dieses Wegs — `RichTextBox` arbeitet auf
`FlowDocument`, verschickt wird HTML. WPF bringt dafür nur `TextRange.Save` mit
`DataFormats.Xaml`/`Rtf` mit; kein HTML. Deshalb als eigener Posten geführt, mit drei
Maßnahmen, die ihn beherrschbar halten:

1. **Formatierumfang bewusst begrenzen** auf genau das, was die Go-Version anbietet
   (Fett/Kursiv/Unterstrichen/Durchgestrichen, Listen, Zitat, Link, Schriftgröße,
   Formatierung löschen). Nur diese Konstrukte müssen abgebildet werden — kein
   allgemeiner XAML→HTML-Übersetzer.
2. **Multipart/alternative senden**: HTML plus automatisch erzeugte Textfassung. MimeKits
   `BodyBuilder` deckt beides ab; Empfänger ohne HTML bekommen etwas Lesbares.
3. **Unit-Tests pro Konstrukt** (Absatz, verschachtelte Liste, Zitat, Link mit Sonderzeichen,
   gemischte Auszeichnung). Das ist der Grund, warum Tests schon in 0.2.0 stehen.

Bekannte Randfälle, die früh zu prüfen sind: eingefügter Inhalt aus dem Browser (bringt
beliebiges XAML mit), verschachtelte Listen, `Hyperlink` mit Umlauten im Ziel, und Umbrüche
(`<br>` gegen `<p>`) beim Zitieren.

---

## 0.5.0 — Kalender & Kontakte

Der größte einzelne Block. Aufteilung nach der getroffenen Entscheidung: Formate über
gepflegte Pakete, Protokoll selbst.

| Feature | Aufwand | Anmerkung |
|---|---|---|
| CalDAV-Protokollschicht | L | **erledigt** — `CalDavService` auf derselben `DavHttp`-Basis wie CardDAV; `calendar-query` mit `time-range`, ETag-Schutz beim Speichern |
| iCalendar lesen/schreiben | S | **erledigt** — `iCal.Net` 5.2.3, inkl. Auflösung von Wiederholungen (RRULE) im sichtbaren Zeitraum |
| Kalenderansichten Monat/Woche/Liste | L | **erledigt** — festes 6-Wochen-Raster (springt beim Monatswechsel nicht), Wochen- und Listenansicht; Woche beginnt montags |
| Termine anlegen/bearbeiten/löschen | M | **erledigt** — eigener Termin-Dialog mit Prüfung auf Zeitlogik; Erinnerungen per Toast offen (Toast-Paket, siehe 0.3.0) |
| Termin-Einladungen (.ics / iMIP) | M | **erledigt** — Banner in der Vorschau, „Zum Kalender hinzufügen" öffnet den Kalender mit vorbelegtem Termin |
| CardDAV-Protokollschicht | M | **erledigt** — `DavHttp` (PROPFIND/REPORT/PUT/DELETE mit ETag), `DavDiscovery` nach RFC 6764, `CardDavService` (Adressbücher listen, Kontakte lesen/anlegen/ändern/löschen) |
| vCard lesen/schreiben | S | **erledigt** — `FolkerKinzel.VCards` 8.2.0, vCard 4.0 |
| CardDAV in den Kontoeinstellungen | S | **erledigt** — Adressfeld je Konto plus „Adressbücher suchen" als Verbindungstest; Anmeldung mit den Zugangsdaten des Kontos |
| Kontakte-Oberfläche (Liste, Bearbeiten, Suche) | M | **erledigt** — eigenes Fenster: Adressbuchwahl, Live-Suche, Anlegen/Bearbeiten/Löschen, Konflikterkennung über ETag |
| vCard-Import/-Export aus Dateien | S | **erledigt** — .vcf einlesen (mehrere Dateien) und die angezeigte Auswahl sichern |
| Geburtstage im Kalender | M | **erledigt** — aus den Kontakten abgeleitet, schreibgeschützt; 29. Februar fällt in Nicht-Schaltjahren auf den 28. |
| Kontaktgruppen/Verteiler | M | **erledigt** — über das vCard-Feld CATEGORIES, Filter in der Kontakte-Ansicht (in Go noch offen) |

> **Verworfen:** die vorhandenen CalDAV-/CardDAV-*Client*-Pakete auf NuGet
> (`jvilalta.CalDAV` 939 Downloads, `BrandUp.CardDav.Client` 1.939). Vierstellige Zahlen
> bedeuten keine Community und keine Wartung — als Fundament einer Kalenderansicht ein
> größeres Risiko als selbst geschriebene Protokollschicht. Die *Server*-Pakete
> (`CalDav.Server.*`, `DevGroup.iCalendar`) lösen die Client-Aufgabe ohnehin nicht.

---

## 0.6.0 — Sicherheit & Systemintegration

| Feature | Aufwand | Anmerkung |
|---|---|---|
| SPF/DKIM/DMARC anzeigen + Spoofing-Warnung | S | **erledigt** — alle `Authentication-Results`-Zeilen ausgewertet (Fehlschlag gewinnt); Leiste unauffällig bei Erfolg, auffällig bei Fehlschlag. Zusätzlich Warnung, wenn der Anzeigename eine fremde Domain vortäuscht |
| Empfänger-TLS-Anzeige (MX + STARTTLS-Probe), MTA-STS | M | **erledigt** — beim Verlassen eines Empfängerfeldes wird je Domain der beste MX aufgelöst, eine Verbindung auf Port 25 aufgebaut, EHLO gesendet und der STARTTLS-Handshake durchgeführt; parallel dazu die MTA-STS-Richtlinie (RFC 8461) geholt und ausgewertet. Bewertung streng: verschlüsselt **ohne** vertrauenswürdiges Zertifikat gilt nicht als gut, nicht erreichbar gilt nicht als unsicher. Eine Verbindung je Domain, 8-s-Budget, Ergebnisse anwendungsweit zwischengespeichert; die Sonde legt nach EHLO/STARTTLS sofort auf und versucht nie eine Anmeldung. Gegen gmail.com real geprüft (STARTTLS, gültiges Zertifikat, `mode: enforce`, MX passt auf das Platzhaltermuster) |
| DANE/TLSA | L | **erledigt, mit eigener DNSSEC-Validierung.** `DnsClient` schied aus: es gibt die rohen RDATA-Bytes nicht heraus, und aus geparsten Objekten kanonisch zurückzuserialisieren ist der Weg, auf dem ein falsches Byte wie ein Angriff aussieht. Also eigene Wire-Schicht (`Services/Dnssec/`): Namen mit kanonischer Ordnung nach RFC 4034 §6.1, Nachrichtenparser mit Auflösung der Komprimierungszeiger, Signaturdaten nach RFC 4035 §5.3.2, Verfahren RSA/SHA-256+512, ECDSA P-256/384, Ed25519 (SHA-1-Verfahren werden **abgelehnt**), Kette ab dem IANA-Wurzelanker, Nichtexistenz-Beweis über NSEC und NSEC3 samt Opt-out. Darauf DANE nach RFC 7672: TLSA unter `_25._tcp.<MX>`, nur Nutzungsarten 2 und 3. Die MX-Auflösung läuft selbst über den Validator — ohne das wäre DANE wirkungslos. Bestätigtes DANE hebt ein fehlendes CA-Vertrauen auf, ein Widerspruch zieht die Bewertung auf „schlecht". **Real geprüft:** `dnssec-failed.org` wird abgelehnt, `ietf.org` und die Kette Root→org validieren, DANE bestätigt bei posteo.de und mail.neuhaus.or.at; mailbox.org (RSA/SHA-1) und gmail.com (MX unsigniert) ergeben korrekt „nicht feststellbar" statt Fehlalarm |
| PGP (Ver-/Entschlüsseln, Signaturen, Key-Manager) | L | **erledigt** — eigener Schlüsselbund unter `%APPDATA%\NMailClient\pgp` (GnuPGs `pubring.kbx` liest MimeKit nicht, und der Bestand des Benutzers bleibt unangetastet). Leseansicht entschlüsselt und prüft Signaturen mit farbiger Leiste; Verfasser hat Schalter für Signieren/Verschlüsseln und **bricht ab**, wenn einem Empfänger der Schlüssel fehlt, statt still im Klartext zu senden. Schlüsselverwalter in den Einstellungen: erzeugen, einlesen, exportieren (nur öffentlicher Teil), entfernen. Mantras im Anmeldeinformationsverwalter. Entwürfe bleiben ungeschützt — für die Empfänger verschlüsselt wären sie für den Verfasser nicht mehr lesbar. **Offen:** die Mantra-Abfrage nutzt den vorhandenen Eingabedialog und zeigt die Eingabe im Klartext; ein `PasswordBox`-Dialog fehlt noch. Gegen einen echten Gegenüber ist nichts erprobt, nur gegen selbst erzeugte Schlüssel |
| Bilder-Whitelist / „Absender vertrauen" | S | **erledigt** — Knopf im Blocker-Banner; Vertrauen gilt je Adresse, nicht je Domain |
| Tray-Icon, Autostart, Ruhezeiten | M | **erledigt** — `Shell_NotifyIcon` per P/Invoke mit eigenem Nachrichtenfenster, ohne Fremdpaket und ohne WinForms in eine WPF-Anwendung zu ziehen. Doppelklick öffnet, Rechtsklick zeigt ein Menü (Öffnen, Neue Nachricht, Beenden). Schliessen legt in den Infobereich statt zu beenden (abschaltbar); dafür läuft die Anwendung auf `ShutdownMode.OnExplicitShutdown`. Autostart über den Run-Schlüssel unter `HKEY_CURRENT_USER` (keine Rechteabfrage beim Anmelden) und startet mit `--minimized` ohne Fenster. Ruhezeiten als reine, geprüfte Rechnerei — der Fall über Mitternacht ist der Normalfall, nicht die Ausnahme |
| Offline-Modus / lokaler Cache | L | **erledigt** — SQLite unter `%LOCALAPPDATA%\NMailClient\cache.db`, WAL-Modus. Nachrichtenkoepfe bei jedem Listenabruf, Koerper beim Oeffnen; Anhaenge bewusst nicht (Groesse, und ohne Verbindung ohnehin wenig Nutzen). Kern des Entwurfs ist die **UIDVALIDITY-Wache**: vergibt der Server UIDs neu, zeigt jede gespeicherte Zeile auf eine andere Nachricht — der Ordner wird dann samt Koerpern und Suchindex verworfen. Faellt die Verbindung aus, liefert `ImapService` den letzten Stand und meldet das als Streifen ueber der Liste; eine abgelehnte **Anmeldung** gilt ausdruecklich nicht als offline, die verlangt eine Handlung. Volltextsuche ueber FTS5 (`unicode61 remove_diacritics 2`, Praefix-Treffer); die Eingabe wird entschaerft, weil ein Suchbegriff keine Abfragesyntax ist. Beim Entfernen eines Kontos wird dessen Post mitgeloescht |
| Einstellungs-Backup (Export/Import) | S | **erledigt** — eine JSON-Datei mit Konten, Etiketten, Bausteinen und Optionen. **Ohne Passwörter** (die liegen im Anmeldeinformationsverwalter); Fremd- und Neuere-Version-Dateien werden abgewiesen |
| Update-Mechanismus (GitHub Releases) + „Ueber"-Ansicht | M | **erledigt** — fragt `releases/latest` ab und vergleicht mit der eigenen Version. Der Vergleich ist eigener Code statt `System.Version`: Vorabversionen sind kleiner als die fertige Fassung (SemVer §11), und der Zahlenvergleich muss numerisch laufen, sonst waere 0.10.0 kleiner als 0.9.0. Entwuerfe und Vorabversionen werden nicht angeboten. **Bewusst ohne Selbstaktualisierung**: es wird gemeldet und die Release-Seite geoeffnet, nicht im Hintergrund heruntergeladen und ausgefuehrt. Der Abruf passiert nur auf Knopfdruck, nicht beim Start. Die „Ueber"-Ansicht zeigt Version, Bauzeitpunkt, alle Ablageorte (kopierbar) und die verwendeten Pakete samt Lizenz. Real geprueft: findet v0.6.3 vom 29.06.2026 |
| Anhang-Archiv: **WebDAV-Ziel** | M | lokales Archiv ist in 0.4.0 erledigt; hier kommt nur die Ablage auf WebDAV dazu (`WebDav.Client`) |
| Empfänger-Autovervollständigung (An/Cc/Bcc) | S | **erledigt** — Vorschlagsliste aus den CardDAV-Kontakten, Bedienung per Pfeiltasten/Eingabe/Klick; Treffer am Wortanfang zuerst. Kontakte werden im Hintergrund geladen, ohne CardDAV bleibt der Composer unverändert nutzbar |
| Eingebauter PDF-Reader | S | **erledigt** — „PDF-Vorschau" im Kontextmenü eines Anhangs, Anzeige im Chromium-Viewer von WebView2; daraus speichern oder extern öffnen |

---

## 0.7.0 — Sieve & mailcow

| Feature | Aufwand | Anmerkung |
|---|---|---|
| **ManageSieve-Client** (Port 4190, STARTTLS, SASL PLAIN) | L | **erledigt** — Protokoll nach RFC 5804 selbst geschrieben, es gibt kein .NET-Paket dafuer. Eigener gepufferter Leser, weil das Protokoll zeilen- **und** byteweise gelesen werden muss: nach `{n+}` folgen genau n Bytes, und die zaehlen in UTF-8, nicht in Zeichen. Ohne STARTTLS wird nicht angemeldet. Anmeldesperre wie bei IMAP/SMTP, `TRYLATER` ausgenommen. Befehle: LISTSCRIPTS, GETSCRIPT, PUTSCRIPT, CHECKSCRIPT, SETACTIVE, DELETESCRIPT, RENAMESCRIPT (mit Nachbildung, falls der Server es nicht kennt). **Gegen mail.neuhaus.or.at gemessen** (Dovecot Pigeonhole, TLS 1.3, 30 Erweiterungen) — dabei bestaetigt: vor STARTTLS meldet Dovecot eine *leere* SASL-Liste, die Faehigkeiten muessen nach der Umstellung neu gelesen werden |
| Sieve-Skripte verwalten | M | **erledigt** — Reiter „Filterregeln" in den Einstellungen: Skriptliste mit Kennzeichnung des aktiven, Editor in Festbreitenschrift, Pruefen ohne Speichern (Warnungen werden angezeigt, auch bei OK), Speichern, Aktivieren, Anlegen, Loeschen. Verbunden wird erst auf Knopfdruck, nicht beim Oeffnen des Dialogs |
| Regelassistent (Wenn-Dann, UND/ODER, alle Aktionen) | L | **erledigt** — Reiter „Regelassistent": Bedingungen (Absender, Empfaenger, Kopie, Betreff, freie Kopfzeile, Umschlag-Empfaenger, Groesse, Nachrichtentext) mit enthaelt / ist genau / passt auf / enthaelt nicht, verknuepft per UND oder ODER; Aktionen Ablegen, Weiterleiten, Verwerfen, Zurueckweisen, Behalten, Etikett, Als gelesen, Ueberspringen. Die `require`-Zeilen werden aus dem tatsaechlich Verwendeten abgeleitet — pauschal alles anzufordern liesse das Skript auf Servern scheitern, die eine Erweiterung nicht koennen. **Entscheidend:** der Assistent schreibt nur zwischen eigenen Markierungen und liest auch nur diesen Block zurueck; handgeschriebenes Sieve bleibt unangetastet. Vor dem Speichern laeuft CHECKSCRIPT. 35 Tests, davon der Rundlauf erzeugen -> ruecklesen -> erzeugen |
| Abwesenheitsnotiz (`vacation`) | S | **erledigt** — Frist, Betreff und Text; mehrzeiliger Text wird als `text:`-Block geschrieben, wobei eine Zeile aus einem einzelnen Punkt entschaerft wird (die wuerde den Block sonst vorzeitig beenden, RFC 5228 §8.1). Die eigenen Adressen des Kontos werden als `:addresses` mitgegeben, sonst antwortet der Server auch auf Verteilerpost |
| mailcow: Verbindung pro Konto (Host + API-Key im Keychain) | M | **erledigt** — Adresse am Konto, **Schluessel im Anmeldeinformationsverwalter**: er ist so maechtig wie ein Administratorzugang und hat in `db.json` nichts verloren. Wird beim Entfernen des Kontos mitgeloescht. Der Schluessel wird bei jedem Aufruf frisch gelesen, liegt also nirgends zwischengespeichert |
| mailcow: Quota, Aliase, App-Passwoerter, Quarantaene | L | **erledigt** — eigener Reiter mit Belegungsbalken (ab 90 % auffaellig), Aliasverwaltung, App-Passwoerter und Quarantaene (Zustellen / Als Spam lernen / Loeschen). Bewusst **nur** eng umrissene Aufrufe: die API koennte Domains und Postfaecher anlegen und loeschen, das gehoert in die Administrationsoberflaeche. Das erzeugte App-Passwort wird einmalig angezeigt, weil mailcow es spaeter nicht mehr herausgibt; es meidet leicht verwechselbare Zeichen, weil es abgetippt wird. 41 Tests auf die Antwortauswertung — mailcow liefert Zahlen mal als Zahl, mal als Zeichenkette, und `msg` mal als Text, mal als Feld |

---

## 1.0.0 — Reife

| Punkt | Aufwand | Anmerkung |
|---|---|---|
| Mehrsprachigkeit DE/EN | M | **XAML vollstaendig, Code offen** — flache JSON-Kataloge im Assembly (324 Eintraege je Sprache), `Loc` mit Indexer, XAML-Kurzform `{i18n:T Schluessel}`, Umschaltung **ohne Neustart**, Spracheinstellung mit „Wie Windows". **Alle 13 Fenster und alle Fenstertitel sind umgestellt** (~370 Texte). Unuebersetzt bleiben absichtlich: Pfeile, Produktname und die Sprachnamen „Deutsch"/„English". Offen: 286 Zeichenketten im Code (Views 120, Services 110, ViewModels 46, Models 10) — die 52 Protokolltexte bleiben bewusst deutsch, damit Logs vergleichbar bleiben. Zwei Stolpersteine unterwegs: `Strings.de.json` wird von MSBuild als kulturspezifische Datei in ein Satelliten-Assembly gepackt (behoben mit `WithCulture="false"`), und die Schlummer-Vorgaben wurden ueber ihre **deutsche Beschriftung** zugeordnet — beim Uebersetzen waere das lautlos auseinandergefallen, jetzt sprachunabhaengige Kennungen mit fuenf Waechter-Tests. Tolgee.io geprueft und verworfen |
| Barrierefreiheit (Tab-Reihenfolge, Screenreader-Namen) | M | in der Go-Version nie adressiert |
| Signierte Installer (MSIX oder Inno Setup) | M | **erledigt bis auf das Zertifikat** — `installer\build.ps1` macht alles in einem Durchlauf: veroeffentlichen, Anwendung signieren, Setup bauen (Inno Setup 6), Setup signieren, pruefen. Signiert mit `Set-AuthenticodeSignature` (kein Windows-SDK noetig), **mit Zeitstempel** — ohne ihn liefe die Signatur mit dem Zertifikat ab. Installation **fuer alle Benutzer** nach `C:\Program Files\N-MailClient`: kostet eine Rechteabfrage, schuetzt die EXE aber vor Austausch durch etwas, das unter dem Benutzerkonto laeuft. Kein Autostart-Haken im Setup — der liefe elevated in das falsche Benutzerprofil; die Anwendung bietet ihn selbst an. Beim Deinstallieren wird nach den Benutzerdaten gefragt, Vorgabe **behalten**, und nur das angemeldete Konto ist betroffen. Version kommt aus dem csproj (`/DAppVersion`), das Skript bricht sonst ab. Ergebnis: 69 MB eigenstaendige EXE, 64 MB Setup. Geprueft: Installation und Deinstallation je Benutzer (vor der Umstellung), Verweigerung ohne Rechte danach, und ein Gegenbeweis — ein gekipptes Byte macht aus der Signatur ein `HashMismatch`. **Offen: das Zertifikat** (derzeit selbstsigniert, daher SmartScreen-Warnung); Wechsel = `-Thumbprint` |

Linux ist **kein Ziel** — WPF und WebView2 dürfen damit ohne Abstraktionsschicht genutzt
werden, ebenso Win32-P/Invoke für Tray und Autostart. Fällt die Entscheidung später anders
aus, ist das ein Avalonia-Port der gesamten View-Schicht (XL), nicht eine Erweiterung.

## Grobsumme

| Meilenstein | Aufwand |
|---|---|
| 0.2.0 Fundament | ~2 Wochen |
| 0.3.0 Mail-Kern | ~4 Wochen |
| 0.4.0 Composer | ~3 Wochen |
| 0.5.0 Kalender/Kontakte | ~6 Wochen |
| 0.6.0 Sicherheit/System | ~5 Wochen |
| 0.7.0 Sieve/mailcow | ~4 Wochen |
| 1.0.0 Reife | ~2 Wochen |

Grob **6 Monate** Vollzeit für Funktionsgleichstand. Das ist die ehrliche Größenordnung —
die Go-Version ist über viele Iterationen gewachsen, und dieser Umfang verschwindet nicht
dadurch, dass die Sprache wechselt. Entlastung bringen vor allem MimeKit (OpenPGP fertig
enthalten) und die REST-Anbindung von mailcow; Mehrkosten entstehen bei CalDAV/CardDAV und
ManageSieve, wo in Go fertige Bausteine existierten.
