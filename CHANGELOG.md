# Changelog

Alle nennenswerten Änderungen an N-MailClient werden hier dokumentiert.
Format angelehnt an [Keep a Changelog](https://keepachangelog.com/de/),
Versionierung nach [SemVer](https://semver.org/lang/de/).

## [0.3.0] - 2026-06-25

### Neu

- **Mailliste nach Datum gruppiert**: Abschnitte „Diese Woche / Letzte Woche /
  Vor zwei Wochen / Älter" mit Sticky-Überschriften (Ordner- und Sammelposteingang).
- **Mehr als 50 Mails laden**: Paginierung mit „Mehr laden"-Button und automatischem
  Nachladen beim Scrollen.
- **Empfänger-TLS-Anzeige**: beim Eingeben einer Adresse wird geprüft, ob der Mailserver
  des Empfängers Transportverschlüsselung (STARTTLS) anbietet — Badge unter dem Feld.
  Prüft nur die Transport-/Hop-Verschlüsselung, kein Ende-zu-Ende (dafür PGP).
- **Bestätigung beim Löschen** von Mails (mit stärkerer Warnung beim endgültigen Löschen),
  in den Einstellungen abschaltbar.
- **Autostart**: „Beim Systemstart öffnen" in den Einstellungen.
- **Backup/Export**: Konten, Etiketten, Kalender/Kontakte-Verbindung, PGP-Schlüssel und alle
  Einstellungen in eine Datei exportieren und wieder importieren.

## [0.2.1] - 2026-06-25

### Behoben

- **App-Icon**: transparente Ränder entfernt, sodass das Taskleisten- und Programm-Icon
  größer und füllender dargestellt wird.

## [0.2.0] - 2026-06-25

### Neu

- **PGP-Verschlüsselung** (OpenPGP.js, PGP/MIME): Schlüsselverwaltung
  (Import/Erzeugen/Export/Löschen, private Schlüssel verschlüsselt via safeStorage);
  eingehende Mails werden entschlüsselt und Signaturen geprüft (Status-Banner in der
  Mailansicht); Versand wahlweise signiert und/oder verschlüsselt (Schalter im Composer).
- **Sieve-Regel-Baukasten**: Wenn-Dann-Regeln (Absender/Empfänger/Cc/Betreff … → in Ordner
  verschieben / als gelesen markieren) erzeugen ein serverseitiges Sieve-Skript.
- **Sendeverfolgung (Zustellstatus)**: optionale Zustellbestätigung (DSN) beim Senden
  anfordern; eingehende Zustellberichte werden als Banner angezeigt
  (zugestellt / fehlgeschlagen / verzögert, mit Empfänger und Diagnose).
- **In-App-Update-UI**: Banner mit Download-Fortschritt und „Jetzt neu starten &
  installieren"; „Nach Updates suchen" in der Über-Ansicht.
- **„Über"-Ansicht** in den Einstellungen mit App-Version und Änderungsverlauf.
- **Anhang-Archiv**: Dateien lassen sich jetzt aus dem Archiv löschen (lokal und WebDAV).

### Geändert

- **Kalender**: überlappende Termine werden in der Wochen- und Tagesansicht nebeneinander
  in Spalten angeordnet statt voll überlagert.

## [0.1.1] - 2026-06-25

### Behoben

- **Sieve-Filter:** Der Button „Neues Skript" hatte keine Funktion (`prompt()` wird
  im Electron-Renderer nicht unterstützt) — ersetzt durch ein Inline-Namensfeld. (#1)
- **Ordnerliste:** Beim Start waren alle Ordner mit Unterordnern aufgeklappt;
  Unterordner starten jetzt eingeklappt. (#2)
- **Tastenkürzel:** Strg+A markiert nun alle Mails in der aktuellen Ordneransicht. (#3)
- **Kalender:** Termine erschienen erst nach 5–10 Sekunden — CalDAV-Login und
  Kalender-Discovery werden jetzt zwischengespeichert, wiederholte Ladevorgänge sind
  deutlich schneller. (#4)

## [0.1.0] - 2026-06-25

Erste Version.

### Mehrere Postfächer

- Beliebig viele IMAP/SMTP-Konten gleichzeitig, mit Autodiscover bei der Einrichtung
- Server-Details, Anzeigename, Signatur und Konto-Farbe pro Konto einstellbar
- Versand auch über zusätzliche Absenderadressen (Aliasse)

### E-Mails lesen

- Gemeinsamer Posteingang über alle Konten, farblich getrennt
- Ordner-Baum zum Auf-/Zuklappen, Posteingang immer oben, Ungelesen-Zähler je Ordner
- Absender-Profilbilder in der Liste (aus Kontakten)
- Sichere HTML-Anzeige; externe Bilder erst auf Knopfdruck
- Quelltext-Ansicht und Drucken

### E-Mails schreiben

- Antworten, Allen antworten, Weiterleiten
- Rich-Text-Editor (fett/kursiv/Listen/Zitate/Links)
- Datei-Anhänge, Empfänger-Autovervollständigung aus Kontakten, optionales BCC
- Automatische Entwürfe, Senden rückgängig machen (einstellbare Verzögerung)

### Organisieren

- Farbige Etiketten, Stern/Beantwortet-Status, Filter „Nur unbeantwortete"
- Kontextmenü: gelesen/ungelesen, löschen, in Ordner verschieben
- Ordner anlegen/umbenennen/verschieben (Drag & Drop), eigene Reihenfolge
- Volltextsuche, Archivieren
- Spam-Training (Junk-Move lernt Spam, „Kein Spam" lernt Ham)
- Serverseitige Filter (Sieve) verwalten — eigener ManageSieve-Client

### Anhänge & Archiv

- Anhänge speichern oder nach Absender automatisch ablegen (lokal oder WebDAV)
- Durchsuchbare Anhang-Archiv-Ansicht, eingebauter PDF-Reader

### Kalender & Kontakte

- CalDAV-Kalender (Monat/Woche/Tag), Termine anlegen/bearbeiten/löschen — auch aus einer Mail
- Termin-Erinnerungen
- CardDAV-Kontakte anlegen/bearbeiten/löschen/suchen, vCard-Fotos als Avatare

### Komfort & Technik

- Live-Erkennung neuer Mails (IMAP IDLE) mit Ton/Desktop-Benachrichtigung
- Heller/Dunkler Modus, umschaltbares Layout (Vorschau rechts/unten), Tastenkürzel
- Zentrale Einstellungen
- Passwörter verschlüsselt über Electron safeStorage; Mail-Logik komplett im Main-Prozess
- Windows-Installer (NSIS) und Linux-.deb, Auto-Update über GitHub Releases
