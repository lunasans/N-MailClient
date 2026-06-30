# Go+Wails Feature-Status (vs. Roadmap bis v0.5.0)

Referenz: `tasks/roadmap.md` im Haupt-Repo.
Markierung: [x] implementiert · [ ] noch nicht in Go-Version

---

## Version 0.1.0

- [x] Einklappbare Ordner-Hierarchie (Baum, Server-Delimiter)
- [x] Bilder: externe Bilder blockiert + „Bilder anzeigen"-Banner
- [x] Anhang „Speichern unter…" (nativer Wails-Dialog)
- [x] Anhang „In Ordner ablegen" (Archiv-Funktion: Ablage nach Absender)
- [x] Eigene „Anhang-Archiv"-Ansicht (eingebundenen Ordner browsen)
- [x] Archiv-Ziel WebDAV (lokale Ablage vorhanden, WebDAV fehlt)
- [x] Eingebauter PDF-Reader (inline via iframe, Vorschau-Button bei PDF-Anhängen)
- [x] Konto-Einstellungen pro Konto (Name, Signatur)
- [x] Ungelesen-Zähler je Ordner (IMAP STATUS), Badge in Ordnerliste
- [x] Rechtsklick-Kontextmenü (gelesen/ungelesen, Spam, Löschen)
- [x] Mail-Etiketten (IMAP-Keywords, farbig, Manager)
- [x] Mail-Versand rückgängig (konfigurierbarer Timer vor Senden)
- [x] Kalender (CalDAV, Monatsansicht, lesend)
- [x] Termin-Erinnerungen (Windows-Toast 15 min vor Termin, aktivierbar in Einstellungen)
- [x] Benachrichtigungssound (IMAP IDLE + WAV/Glocke-Toggle)
- [x] Rich-Text-Composer (Fett/Kursiv/Unterstr./Strikethrough/Listen/Zitat/Link/Schriftgröße/Formatierung löschen)
- [x] Mail-Quelltext-Ansicht
- [x] Anhang-Archiv durchsuchbar
- [x] Mehrere Konten in der Sidebar (nicht Dropdown)
- [x] Drucken-Funktion
- [x] Antworten / Allen Antworten / Weiterleiten / Stern-Markierung
- [x] Mail archivieren (Move in Archive-Ordner)
- [x] Mehrere Konten in der Übersicht (Konto → Ordner → weiteres Konto)
- [x] Mit Alias absenden (Aliases in Konto-Einstellungen, Composer-Dropdown)
- [x] Kontextmenü: Mail in Ordner verschieben
- [x] Keine Emojis (rein textuelle Icons)
- [x] Konto in den Einstellungen konfigurierbar (Server/Ports/User/Passwort)
- [x] Filter „Nur unbeantwortete" (Toggle-Button in Listenleiste)
- [x] Termine in Kalender eintragbar (aus Mail heraus via ICS)
- [x] Inbox immer als oberster Ordner
- [x] Datei-Anhang im Composer (Anhänge beim Verfassen / Drag & Drop)
- [x] Ordner erstellen, löschen, umbenennen
- [x] Ordner-Reihenfolge in Kontoeinstellungen frei festlegen
- [x] Optionales BCC-Feld
- [x] Entwürfe (alle 10 s auto-save bis Versand)
- [x] Kontakte (CardDAV, lesend + schreibend, Suche)
- [x] Empfänger-Autovervollständigung aus Kontakten (An/Cc/Bcc)
- [x] Einstellungen zentralisiert (ein Dialog: Allgemein / Konten / Sieve / Kalender & Kontakte)
- [x] Kontakte anlegen/bearbeiten/löschen (CardDAV-Schreibzugriff)
- [x] Kalender: Termine bearbeiten/löschen
- [x] Kalender: Wochenansicht (Zeitraster, 3-stufiger Umschalter Monat/Woche/Liste)
- [x] Volltextsuche über Mails (IMAP SEARCH, live debounced)
- [x] Mehrere Mailansichten (Vorschau rechts oder unten, Toggle in Einstellungen)
- [x] Sieve-Filter verwalten (ManageSieve-Client, Port 4190 STARTTLS+SASL PLAIN)
- [x] Initialen-Avatar der Absender in der Mailliste (farbige Kreise mit Initialen)
- [x] Dark Mode
- [x] Tastenkürzel (Entf, Strg+Enter senden, j/k/Pfeile Navigation, r Antworten, f Weiterleiten, e Archivieren, u Ungelesen, / Suche)
- [x] Spam-Training (Junk-Move vorhanden; „Kein Spam"-Ham-Button wenn Spam-Ordner aktiv)

---

## Version 0.1.1 (Fixes)

- [x] #1 Sieve: „+Neues Script"-Button funktioniert
- [x] #2 Sidebar: Unterordner beim Start eingeklappt
- [x] #3 Strg+A markiert alle Mails (+ Ctrl+Click Einzelauswahl, Batch-Aktionen)
- [x] #4 Kalendereinträge erscheinen verzögert (CardDAV-Cache)

---

## Version 0.2.0

- [x] Löschfunktion im Anhang-Archiv
- [x] „Über"-Ansicht (Version + Changelog)
- [x] In-App-Update-UI (GitHub Releases API, „Nach Updates suchen"-Button)
- [x] PGP-Verschlüsselung (golang.org/x/crypto/openpgp; Key-Manager in Settings, Composer-Toggle, Decrypt-Button)
- [x] Sendeverfolgung / DSN (Disposition-Notification-To header, Checkbox im Composer)
- [x] Sieve-Regelassistent (Wenn-Dann-UI → Sieve-Skript)
- [x] Überlappende Termine in Wochenansicht in Spalten (anteilige Breite)

---

## Version 0.3.0

- [x] Mailliste nach Datum gruppieren (Diese Woche / Letzte Woche / …)
- [x] Mehr als 50 Nachrichten: Paginierung + „Mehr laden"
- [x] Bestätigungsabfrage beim Löschen (konfigurierbar)
- [x] Autostart beim Systemstart (Windows-Registry HKCU\Run, Checkbox in Einstellungen)
- [x] Empfänger-TLS-Anzeige (MX + STARTTLS-Probe Port 25, gecacht)

---

## Version 0.4.0

- [x] Mail-Übersetzung (LibreTranslate, self-hosted, Server-URL + API-Key)
- [x] Trennlinie zwischen Mailliste und Vorschau (CSS-Trenner)

---

## Version 0.5.0

- [x] HTML-Mails übersetzen (format=html, Markup bleibt)
- [x] Tray-Icon (Win32 Shell_NotifyIcon, kein externes Paket, Doppelklick = Fenster zeigen)
- [x] MTA-STS auswerten (TLS-Badge + mta-sts.txt fetch, Anzeige im Composer-TLS-Bar)
- [x] PGP: entschlüsselte Anhänge + detached-Signaturen (via PGPDecryptBody)
- [x] Sieve-Regelassistent: weitere Aktionen (verschieben/verwerfen/weiterleiten/reject)
- [x] Konversations-/Thread-Ansicht (gleiche Betreffe, Anzahl-Badge, Umschalter)
- [x] Abwesenheitsnotiz / Auto-Responder (Sieve `vacation`)
- [x] Termin-Einladungen (.ics / iMIP): Banner + „Zum Kalender hinzufügen"
- [x] Signatur pro Alias (wechselt automatisch wenn Absender im Composer geändert)
- [x] Rückgängig-Toast für Löschen/Archivieren/Verschieben
- [x] vCard-Import/-Export
- [x] Geburtstage aus Kontakten im Kalender
- [x] Hover-Schnellaktionen in der Liste (Archivieren/Löschen on-hover)
- [x] Benachrichtigungen nur Posteingang + Ruhezeiten
- [x] Bilder-Whitelist (vertrauenswürdige Absender, „Absender vertrauen"-Button)
- [x] Einstellungen-Backup / Export+Import (JSON)
- [x] Neue-Mail-Benachrichtigung (Windows Toast via go-toast, IMAP-Polling 60 s)

---

Version 0.6.0 (veröffentlicht)

- [x] SPF/DKIM/DMARC-Ergebnis anzeigen + Spoofing-Warnung (Authentication-Results parsen, Anzeigename≠Adresse)  [Vorhandenes nutzen]
- [x] Rechtschreibprüfung im Composer (WebView2-Spellcheck, Sprache wählbar: DE/EN/Auto/Aus)  [Konten & Protokoll]
- [x] Inbox-Kategorien / Tabs (Allgemein, Werbung, Newsletter, Soziales …) — automatische Einordnung anhand von Headern (List-Unsubscribe, Precedence: bulk, Auto-Submitted) + Absender-Heuristik; Tab-Leiste über der Liste, Kategorie pro Absender überschreibbar  [Produktivität]
- [x] Offline-Modus / lokaler Cache  [Konten & Protokoll] — Maillisten-Cache (Summaries) + Nachrichten-Bodies (Detail je UID) als JSON; Sofort-Anzeige + transparenter Offline-Fallback bei Liste und beim Öffnen
- [x] Empfänger-TLS: DANE/TLSA (braucht DNSSEC-validierenden Resolver) — konfigurierbarer Resolver, AD-Flag-Prüfung

Version 0.7.0

- [x] Links aus E-Mails im Systembrowser öffnen (statt in der Mail-Ansicht) — via Wails BrowserOpenURL
- [x] Smart-/virtuelle Ordner (feste Schnellfilter Ungelesen/Markiert/Ungelesen&Markiert, kontoweit über alle Ordner; Badges, lazy berechnet)  [Produktivität]
- [x] Anhang-Archiv überarbeitet: kontobezogener Ordner (+Ordnerauswahl), rekursive Anzeige, Ablage nach Absender/Jahr/Monat, nach Absender gruppierte Ansicht, Doppelklick zum Öffnen, WebDAV mit MKCOL
- [] Follow-up / Wiedervorlage-Markierung mit Erinnerung  [Produktivität]
- [] Geplanter Versand (zu bestimmtem Zeitpunkt senden; baut auf Undo-Send/Scheduler auf)  [Vorhandenes nutzen]
- [] Snooze / „später erinnern" (Mail verschwindet, kommt zur gewählten Zeit zurück)  [Produktivität]
- [] Vorlagen / Textbausteine (Canned Responses)  [Produktivität]
- [] Anpassbare Listendichte / Spalten  [Komfort]
- [] OAuth2 (Gmail/Microsoft 365) für Konten  [Konten & Protokoll]
- [] Listen-Virtualisierung für sehr große Ordner
- [] Sieve-Baukasten: Mehrfachbedingungen UND/ODER
- [] Kontaktgruppen/Verteiler aus vCards  [Kontakte/Kalender]
- [] Aliase vom Server abrufen — Button „Vom Server abrufen" im Aliase-Abschnitt der
  Konto-Einstellungen. Hintergrund: IMAP/SMTP kennen kein Alias-Konzept, JMAP wird von
  mailcow noch nicht unterstützt. Umsetzung via mailcow-Admin-API: `GET /api/v1/get/alias/all`
  mit Read-only-API-Key, filtern auf `goto` = Konto-Adresse (liefert präzise alle Aliase,
  auch ungenutzte). Benötigt pro Konto mailcow-Host + API-Key (API-Key im OS-Keychain
  ablegen, nicht in `db.json`).
- [] Mailcow-Integration Phase 1 (Modus A: eigener API-Key) — siehe tasks/mailcow-konzept.md:
      Mailcow-Verbindung pro Konto (Host + API-Key + Test, Key via safeStorage);
      App-Passwörter verwalten; Alias-Verwaltung anlegen/Ziel ändern/löschen (Auflisten/Abruf bereits in 0.6.0);
      Wegwerf-/temporäre Aliase; Quarantäne ansehen & verwalten; Postfach-Kontingent (Quota) anzeigen

Version 1.0.0

- [] Mehrsprachigkeit (i18n) DE/EN
- [] Linux-Build / .deb (Cross-Platform-Port) — Windows-spezifischen Code (Autostart-Registry,
  DANE System-DNS via IP-Helper-API, Tray, DWM-Titelleiste, go-toast) hinter Build-Tags
  kapseln + Linux-Pendants (autostart .desktop, /etc/resolv.conf, notify-send); CI um
  Linux-Job (gtk3/webkit2gtk) + .deb-Paketierung (nfpm) erweitern. Baut aktuell nicht für Linux.

## Mailcow-API-Integration (zukünftige Versionen)

Hinweis Architektur: Mailcow-API-Keys sind admin-/domain-admin-gebunden + IP-Allowlist; kein
reiner End-User-Token. Auth-Header `X-API-Key`, Basis `https://<host>/api/v1/`,
read-only vs. read-write Keys. Für Endkunden ggf. Domain-Admin-Delegation oder Backend-Proxy.

Priorität hoch:
- [] App-Passwörter verwalten (protokollspezifisch IMAP/SMTP/CalDAV/CardDAV/Sieve erzeugen/anzeigen/widerrufen — auch fürs Onboarding/Setup)
- [] Wegwerf-/temporäre Alias-Adressen anlegen ("Burner"-Mails mit Ablaufdatum, `time_limited_alias`)
- [] Alias-Verwaltung: anlegen/Ziel ändern/löschen (reines Auflisten/Abrufen bereits in 0.6.0 „Aliase vom Server abrufen")
- [] Quarantäne ansehen & verwalten (zurückgehaltene Mails listen, freigeben, als Ham lernen, löschen)

Priorität mittel:
- [] Spam-Whitelist/Blacklist über Mailcow-API pflegen (`domain-policy`)
- [] Spam-Empfindlichkeit pro Postfach (Spam-Score-Schwellwert lesen/setzen)
- [] Mail-Import-Assistent: Sync-Jobs (IMAP) anlegen/überwachen
- [] Postfach-Kontingent (Quota-Nutzung) anzeigen
- [] Sieve-Filter verwalten — via ManageSieve (Port 4190 + App-Passwort), nicht über REST

Priorität niedrig / optional:
- [] Mailbox-Passwort aus dem Client ändern
- [] Admin-Modus (Domains, DKIM, TLS-Policy, Logs/Queue/Fail2ban) nur für Domain-Admins



## Zusammenfassung

| Version | Gesamt | Implementiert | Offen |
|---------|--------|---------------|-------|
| 0.1.0   | 30     | 30            | 0     |
| 0.1.1   | 4      | 4             | 0     |
| 0.2.0   | 7      | 7             | 0     |
| 0.3.0   | 5      | 5             | 0     |
| 0.4.0   | 2      | 2             | 0     |
| 0.5.0   | 18     | 18            | 0     |
| **Total** | **66** | **66**      | **0** |


