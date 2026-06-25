# Changelog

Alle nennenswerten Änderungen an N-MailClient werden hier dokumentiert.
Format angelehnt an [Keep a Changelog](https://keepachangelog.com/de/),
Versionierung nach [SemVer](https://semver.org/lang/de/).

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
