# Neuhaus MailClient — todo

## Aktuelle Analyse 2026-06-24
- [x] Architektur und Modulgrenzen erfassen
- [x] Mail-, Archiv- und Credential-Flows prüfen
- [x] Renderer-/IPC-Oberfläche prüfen
- [x] Build-/Typecheck-Zustand verifizieren
- [x] Risiken, Stärken und nächste sinnvolle Schritte dokumentieren

### Review 2026-06-24
- Architektur: Electron Main kapselt IMAP/SMTP/Credentials/Dateisystem; Preload stellt eine
  typisierte IPC-Fassade bereit; Renderer nutzt React + Zustand.
- Stärken: klare Prozessgrenzen, keine Passwörter im Renderer, DOMPurify fuer Mail-HTML,
  externe Bilder standardmaessig blockiert, Anhänge werden vor lokaler Ablage segmentweise
  sanitisiert.
- Kritisch: `npm.cmd run typecheck` ist rot, weil `MailList.tsx` alte Store-Methoden
  (`toggleSeen`, `deleteMessage`, `markSpam`) nutzt; der Store bietet inzwischen
  `setMessagesSeen`, `removeMessages`, `spamMessages`.
- Build: `npm.cmd run build` scheitert vor der eigentlichen Kompilierung mit EPERM beim
  Leeren von `out/main/index.js`; nach beendetem Dev-Server sind keine sichtbaren
  Electron-/Node-Prozesse mehr aktiv, ein direkter Löschversuch meldet aber weiter
  "Zugriff verweigert". Das spricht fuer einen Datei-/Cloud-Sync-Lock oder ein Windows-Rechteproblem.
- Risiken: Autodiscover akzeptiert TLS-Zertifikate beim Probe bewusst ungeprueft
  (`rejectUnauthorized: false`), lokale Archiv-Dateioperationen vertrauen bei Open/Reveal
  auf Renderer-gelieferte Pfade, und `safeStorage` faellt in Dev-Umgebungen auf base64
  markierten Klartext zurueck.
- Naechste Schritte: zuerst Store/API-Drift in `MailList.tsx` beheben, dann Electron-Prozesse
  beenden und Typecheck + Build erneut ausfuehren; danach Encoding/UI-Texte bereinigen.

## MVP (in Arbeit)
- [x] Scaffolding: electron-vite + React + TS + Tailwind
- [x] Datenhaltung: JSON-Store im userData + safeStorage (DPAPI) für Passwörter
- [x] Account-Management: anlegen / auflisten / entfernen (generisch, nichts hardcodiert)
- [x] Autodiscover (mail./imap./smtp.<domain>) + manuelle Korrektur
- [x] IMAP lesen: Ordner, Nachrichtenliste, Nachricht (mailparser), Gelesen-Flag
- [x] SMTP senden (nodemailer) + Kopie in „Gesendet" (IMAP APPEND)
- [x] HTML-Mails mit DOMPurify sanitisiert rendern
- [x] `npm install` + Typecheck + `npm run build` (alles grün)
- [x] `npm run dev` verifiziert: Electron-Fenster bootet fehlerfrei
- [ ] End-to-End-Test gegen echtes Mailcow-Konto (durch dich)

## Architektur
- Main-Prozess: gesamte Mail-/Credential-Logik (src/main/services, src/main/ipc)
- Preload: typisierte contextBridge (window.api)
- Renderer: React-UI, sanitisiert HTML selbst, kein direkter Netzwerkzugriff

## Erweiterungen (umgesetzt)
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
- [x] Mehrfachauswahl (Klick / Strg+Klick / Shift+Bereich) + Bulk-Aktionen
      (gelesen/Spam/löschen) in Toolbar und Kontextmenü; IMAP-Ops UID-Array-fähig
- [x] Drag&Drop von (mehreren) Mails auf einen Ordner → Verschieben (mail:move),
      Drop-Ziel wird hervorgehoben, Ungelesen-Badges in Quelle/Ziel angepasst

## Später (Post-MVP)
- OAuth2 (Gmail / Microsoft Graph)
- Lokaler Cache + Volltextsuche (Umstieg JSON → SQLite)
- IMAP IDLE / Auto-Sync / Benachrichtigungen
- Inline-Bilder via cid:, Rich-Text-Composer, Threads
- Konto bearbeiten (Archiv-Ordner auch in den Einstellungen ändern)

## Review
- MVP komplett gebaut: Multi-Account (generisch), Autodiscover+manuell, IMAP lesen,
  SMTP senden + Sent-Kopie, DOMPurify-Sanitizing.
- Verifiziert: `npm run typecheck` grün, `npm run build` grün, Electron-Fenster bootet ohne Fehler.
- Bewusste Plan-Abweichung: JSON-Store statt better-sqlite3 (kein nativer Build unter Windows).
- Offen für dich: echter E2E-Test (Konto anlegen/lesen/senden) gegen Mailcow.
- Start in dieser Umgebung: `env -u ELECTRON_RUN_AS_NODE npm run dev` (siehe lessons.md).
