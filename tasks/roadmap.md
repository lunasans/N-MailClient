Initial Version 0.1.0

- [x] Einklappbare Ordner-Hierarchie (Baum, Server-Delimiter)
- [x] Bilder: externe Bilder blockiert + „Bilder anzeigen"-Banner
- [x] Anhang „Speichern unter…" (nativer Dialog)
- [x] Anhang „In Ordner ablegen": pro Konto eingebundener Ordner,
      Ablage nach Absender sortiert (<Ordner>/<Absender>/<Datei>), Kollisions-Suffix
- [x] Eigene „Anhang-Archiv"-Ansicht im Client: eingebundenen Ordner browsen
      (nach Absender gruppiert), Dateien öffnen, im Explorer anzeigen, Ordner ändern
- [x] Archiv-Ziel wahlweise LOKAL oder WebDAV (Nextcloud etc.), pro Konto;
      WebDAV-Setup mit Verbindungstest, Passwort via safeStorage verschlüsselt
      (webdav v4 = CommonJS wegen Electron-Node-20)
- [x] Eingebauter PDF-Reader (Chromiums nativer Viewer in eigenem Fenster):
      PDF-Anhänge in der Mail + archivierte PDFs (lokal/WebDAV) direkt ansehen
- [x] Konto-Einstellungen pro Konto (⚙): Anzeigename + Signatur;
      Signatur wird im Composer mit „-- " vorbefüllt (editierbar)
- [x] Ungelesen-Zähler je Ordner (IMAP STATUS), Badge in der Ordnerliste,
      live aktualisiert beim Lesen/Markieren/Löschen
- [x] Rechtsklick-Kontextmenü auf E-Mails: gelesen/ungelesen, als Spam (→ Junk),
      löschen (→ Papierkorb bzw. \Deleted+Expunge)
- [x] Mail Etiketten (IMAP-Keywords, farbig, Manager + Kontextmenü)
- [x] Mail versand(einstellbare zeit in den einstellungen ) rückgängig machen 
- [x] Kalender einbindung (CalDAV/Nextcloud — Monatsansicht, lesend)
- [x] Termin erinnerung 
- [x] Benachrichtigungssound (IMAP IDLE Live-Erkennung + WAV/Notification, Glocken-Toggle) 
- [x] Text Editor (Rich-Text: Fett/Kursiv/Listen/Zitat/Links → HTML-Mails)
- [x] Mail Quelltext Ansicht
- [x] Anhangsarchiv durchsuchbar 
- [x] Bei mehreren Konten: Gemeinsamer Posteingang (mit Konto Farbe makriert)
- [x] Druckenfunktion 
- [x] Auf mail antworten, allen Antworten, weiterleiten, markieren (Stern + beantwortet-Status)
- [x] Mail Archivieren 
- [x] Mehrere Konten in der Übersicht anzeigen (mail adresse - Order -  weiteres Konto - Ornder) nicht als Dropdown 
- [x] mit alias Absender versenden 
- [x] Im kontextmenü mail verschieben in Ordner
- [x] Keine Emojis (lucide-react Icons) 
- [x] Dropdown in der oberen leiste entfernen 
- [x] Konto in den Einstellungen einstellbar (Server/Ports/Benutzer/Passwort/Name/Signatur)  
- [x] Mail markieren auf die noch nicht geantwortet wurde (Filter „Nur unbeantwortete")
- [x] Termine in Kalender eintragbar (aus der mail herraus)
- [x] Inbox immer als  ober Ordner 
- [x] Datei Anhang in Mail (beim Verfassen/Antworten/Weiterleiten)
- [x] Ordner erstllen, löschen, umbenennen, per Drag and Drop verschieben. 
- [x] Reihenfolge der Ordner in den Kontoeinstellungen selbst festlegen können. 
- [x] Optionales BCC Feld 
- [x] Entwürfe (Mail beim Schreiben alle 10 Sekunden in Entwürfe speichern bis abgesendet wurde)
- [x] Kontakte (CardDAV — lesend, Suche, Klick auf E-Mail öffnet Composer)
- [x] Empfänger-Autovervollständigung aus Kontakten (An/Cc/Bcc) + vCard-Fotos als Avatare
- [x] Einstellungen zentralisieren (ein Dialog: Allgemein / Konten / Etiketten / Kalender & Kontakte)
- [x] Kontakte anlegen/bearbeiten/löschen (CardDAV-Schreibzugriff)
- [x] Kalender: Termine bearbeiten/löschen
- [x] Kalender: Wochen-/Tagesansicht (Zeitraster, Monat/Woche/Tag-Umschalter)
- [x] Volltextsuche über Mails (IMAP SEARCH)
- [x] Mehrere verschiedene Mailansichten (Vorschau rechts / unten, in Einstellungen)
- [x] Sieve-Filter verwalten (eigener ManageSieve-Client, Port 4190 STARTTLS+SASL PLAIN; Skripte auflisten/laden/bearbeiten/anlegen/aktiv setzen/löschen in den Einstellungen)
- [x] Profilbilder der Absender in der Mailliste (aus Kontakt-Fotos, sonst Initiale)
- [x] Dark Mode
- [x] Tastenkürzel (Entf löschen, Strg+Enter senden, j/k bzw. Pfeile navigieren)
- [x] Spam-Training (Junk-Move lernt Spam; „Kein Spam" → Posteingang lernt Ham via Dovecot/Rspamd antispam)

Version 0.1.1 (nur Fixes)

- [x] #1 Filter (Sieve): Button „+Neues Script" hat keine Funktion (prompt() in Electron-Renderer durch Inline-Namensfeld ersetzt)
- [x] #2 Sidebar: beim Start sind alle Ordner mit Unterordnern aufgeklappt (Unterordner werden jetzt standardmäßig eingeklappt)
- [x] #3 Tastenkombi Strg+A markiert nicht alle Mails (Select-all im Mail-View ergänzt)
- [x] #4 Kalendereinträge erscheinen erst nach 5–10 Sekunden (CalDAV-Login + Kalender-Discovery werden jetzt gecacht)

Version 0.2.0 

- [x] #5 Löschfunktion im Anhangsarchiv (Dateien lokal/WebDAV löschen; Löschen-Button pro Datei mit Bestätigung, leere Absender-Ordner werden lokal aufgeräumt)
- [x] „Über"-Ansicht in den Einstellungen (App-Version + Änderungsverlauf/Changelog, zur Build-Zeit injiziert)
- [x] In-App-Update-UI (Banner „Update verfügbar", Download-Fortschritt, „Jetzt neu starten & installieren", „Nach Updates suchen" in der Über-Ansicht) — electron-updater-Events werden an den Renderer gebroadcastet
- [ ] PGP verschlüsselung (OpenPGP.js, PGP/MIME) — Teil 1/3 erledigt: Schlüsselverwaltung (Import/Erzeugen/Export/Löschen, privat via safeStorage); offen: Lesen (entschlüsseln/Signatur prüfen), Senden (signieren/verschlüsseln)
- [] Sendeverfolgung 
- [] Mehrsprachigkeit (i18n) DE/EN
- [] Mail-Regeln/Filter lokal (auto-verschieben/etikettieren)
- [] Backup/Export der App-Einstellungen
- [] Überlappende Termine in der Wochenansicht werden derzeit noch voll überlagert (keine Seite-an-Seite-Anordnung)

## Mailcow-API-Integration (zukünftige Versionen)

Hinweis Architektur: Mailcow-API-Keys sind admin-/domain-admin-gebunden + IP-Allowlist; kein
reiner End-User-Token. Auth-Header `X-API-Key`, Basis `https://<host>/api/v1/`,
read-only vs. read-write Keys. Für Endkunden ggf. Domain-Admin-Delegation oder Backend-Proxy.

Priorität hoch:
- [] App-Passwörter verwalten (protokollspezifisch IMAP/SMTP/CalDAV/CardDAV/Sieve erzeugen/anzeigen/widerrufen — auch fürs Onboarding/Setup)
- [] Wegwerf-/temporäre Alias-Adressen anlegen ("Burner"-Mails mit Ablaufdatum, `time_limited_alias`)
- [] Alias-Verwaltung (eigene Aliase auflisten/anlegen/Ziel ändern/löschen)
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