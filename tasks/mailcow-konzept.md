# Konzept: Mailcow-Integration

Status: Entwurf · Zielversionen: ab 0.4.0 (nach den 0.3.0-Punkten)

Dieses Dokument beschreibt, **wie** N-MailClient Mailcow-spezifische Funktionen
(App-Passwörter, Aliase, Quarantäne, Spam-Einstellungen …) anbinden kann, **welche
Architektur** dafür nötig ist und in **welchen Phasen** das sinnvoll umgesetzt wird.

---

## 1. Ziel

Über das reine IMAP/SMTP/CalDAV/CardDAV hinaus soll der Client Komfort-/Verwaltungs-
Funktionen anbieten, die nur die **Mailcow-Admin-API** (bzw. das User-Panel) bereitstellt —
z. B. App-Passwörter erzeugen, Wegwerf-Aliase anlegen, Quarantäne sichten, Spam-Listen pflegen.

Wichtig: N-MailClient bleibt **provider-neutral**. Die Mailcow-Funktionen sind ein
**optionales Modul**, das pro Konto aktiviert wird und nur erscheint, wenn eine
Mailcow-Verbindung konfiguriert ist. Konten ohne Mailcow funktionieren unverändert.

---

## 2. Architektur & Authentifizierung (der Knackpunkt)

Die Mailcow-REST-API ist **nicht** für End-User gedacht:

- Auth per Header **`X-API-Key`**, Basis `https://<host>/api/v1/`.
- API-Keys sind **global Admin** oder **Domain-Admin** gebunden, plus optionale **IP-Allowlist**.
- Es gibt **read-only** und **read-write** Keys.
- **Kein** per-Postfach-OAuth-Token: Ein Endnutzer kann sich nicht mit „seinem" Mailkonto
  an der API anmelden und nur seine eigenen Ressourcen verwalten.

Daraus folgen drei mögliche Betriebsmodi:

### Modus A — Self-Hoster / Admin (empfohlen für Phase 1)
Der Nutzer ist **Betreiber seiner eigenen Mailcow** (oder Domain-Admin) und hinterlegt
**seinen eigenen API-Key** in der App. Damit sind alle Admin-Funktionen verfügbar, die
der Key-Scope erlaubt.

- Realistisch, schnell umsetzbar, kein Backend nötig.
- Key wird **verschlüsselt via safeStorage** gespeichert (wie IMAP-Passwörter).
- Standard read-write; read-only-Key → nur Lesefunktionen, Schreibaktionen ausgegraut.
- **Zielgruppe:** das Neuhaus-Setup selbst und andere Self-Hoster.

### Modus B — Backend-Proxy / Delegation (Phase 2, optional)
Für **Mehrbenutzer/Endkunden** ohne Admin-Rechte: Ein **serverseitiger Proxy** hält den
Admin-Key, authentifiziert den Nutzer (z. B. über sein Mail-Login / OAuth) und gibt nur
**auf sein Postfach beschränkte**, abgesicherte Endpunkte frei.

- Erfordert eine zusätzliche Server-Komponente (eigenes kleines API-Gateway).
- Nötig, sobald nicht jeder Nutzer Admin seiner Mailcow ist.
- Größerer Aufwand; bewusst spätere Phase.

### Modus C — User-Panel-Funktionen ohne Admin-API
Mailcow hat ein **User-Panel** (`/user`) mit Selbstbedienung: App-Passwörter, Spam-Aliase,
Spam-Score, TLS-Policy, Sieve. Diese laufen aber **session-/CSRF-basiert**, es gibt **keine
stabile REST-API** dafür → Automatisierung wäre fragil (Form-Posts, bricht bei Updates).

- **Nicht empfohlen** als Hauptweg.
- Ausnahme: **Sieve** decken wir bereits **ohne** Mailcow-API über **ManageSieve** ab
  (Port 4190) — das bleibt der Weg der Wahl und ist provider-neutral.

> **Empfehlung:** Phase 1 = **Modus A**. Funktionen klar als „Mailcow-Verwaltung"
> kennzeichnen und nur bei konfiguriertem Key anzeigen. Modus B nur, falls echtes
> Endkunden-Szenario entsteht.

---

## 3. Konto-Integration (UI/Settings)

- Neuer Abschnitt **Einstellungen → Mailcow** (pro Konto, optional).
  - Felder: Host (Vorbelegung aus IMAP-Host), API-Key (Passwortfeld), „Verbindung testen".
  - Test: `GET /api/v1/get/status/containers` oder `GET /api/v1/get/mailbox/<eigene-adresse>`
    → prüft Key + Erreichbarkeit; meldet read-only vs read-write, falls erkennbar.
  - Key verschlüsselt via safeStorage; nie im Renderer im Klartext.
- Ist kein Key gesetzt → alle Mailcow-Ansichten sind ausgeblendet/deaktiviert.
- Hinweistext zur IP-Allowlist (häufige Fehlerquelle: Key funktioniert nur von erlaubten IPs).

---

## 4. Funktionsblöcke

Reihenfolge = Priorität. Jeder Block: Zweck · API · UI · Schreibrechte.

### 4.1 App-Passwörter (Priorität hoch)
- **Zweck:** protokoll-spezifische Passwörter (IMAP/SMTP/DAV/EAS) erzeugen/anzeigen/widerrufen —
  auch fürs eigene Onboarding statt des Hauptpassworts.
- **API:** `add/app-passwd`, `get/app-passwd/all/<mailbox>`, `edit/app-passwd`, `delete/app-passwd`.
- **UI:** Liste pro Postfach (Name, Protokoll-Scopes, aktiv), „Neu" (zeigt Passwort **einmal**),
  „Widerrufen".
- **Schreibrecht** nötig.

### 4.2 Wegwerf-/temporäre Aliase (Priorität hoch)
- **Zweck:** „Burner"-Adressen mit Ablaufdatum (Newsletter, Anmeldungen).
- **API:** `add/time_limited_alias`, `get/time_limited_alias/<mailbox>`, Löschen via passendem Endpoint.
- **UI:** Liste (Adresse, Ablauf), „Neue Wegwerf-Adresse", „Löschen"; ein Klick →
  Adresse in die Zwischenablage / direkt als Absender-Alias nutzbar.
- **Schreibrecht** nötig.

### 4.3 Alias-Verwaltung (Priorität hoch)
- **Zweck:** eigene dauerhafte Aliase auflisten/anlegen/Ziel ändern/löschen.
- **API:** `get/alias/all` (gefiltert auf eigene), `add/alias`, `edit/alias`, `delete/alias`.
- **UI:** Tabelle (Alias → Ziel, aktiv), CRUD. Verknüpfung mit der bestehenden
  **Alias-Absender**-Funktion (Composer-„Von"): neu angelegte Aliase automatisch dort anbieten.
- **Schreibrecht** nötig.

### 4.4 Quarantäne (Priorität hoch)
- **Zweck:** zurückgehaltene Mails sichten, freigeben, als Ham lernen, löschen.
- **API:** `get/quarantine/all`, `edit/qitem` (deliver/learn ham), `delete/qitem`.
- **UI:** Eigene Ansicht „Quarantäne" (ähnlich Mailliste): Absender/Betreff/Score/Datum,
  Aktionen „Zustellen", „Kein Spam (lernen)", „Löschen". Badge mit Anzahl.
- **Schreibrecht** für Aktionen; Liste auch read-only.

### 4.5 Spam-Whitelist/Blacklist (Priorität mittel)
- **Zweck:** Absender/Domains pro Postfach auf Allow-/Blocklist setzen.
- **API:** Domain-/Mailbox-Policy-Endpunkte (`add/domain-policy`, Mailbox-WL/BL-Listen, Löschen).
- **UI:** Zwei Listen (Whitelist/Blacklist) mit Hinzufügen/Entfernen; Kontextmenü-Eintrag
  in der Mailliste „Absender immer zulassen / blockieren".
- **Schreibrecht** nötig.

### 4.6 Spam-Empfindlichkeit pro Postfach (Priorität mittel)
- **Zweck:** Spam-Score-Schwellwerte (greylist/add header/reject) lesen/setzen.
- **API:** Mailbox-Spam-Settings (`edit/mailbox` bzw. Rspamd-Settings/quarantine-Schwellen).
- **UI:** drei Schieberegler/Felder (low/medium/high) in den Mailcow-Einstellungen.
- **Schreibrecht** nötig.

### 4.7 Postfach-Kontingent (Quota) anzeigen (Priorität mittel)
- **Zweck:** Belegung/Quota sichtbar machen.
- **API:** aus `get/mailbox/<adresse>` (Felder `quota`, `quota_used`).
- **UI:** Fortschrittsbalken in der Über-/Konto-Ansicht; ggf. Warnung bei >90 %.
- **Read-only** ausreichend.

### 4.8 Mail-Import-Assistent / Sync-Jobs (Priorität mittel)
- **Zweck:** IMAP-Sync-Jobs (Migration von anderem Postfach) anlegen/überwachen.
- **API:** `add/syncjob`, `get/syncjobs/all`, `edit/syncjob`, `delete/syncjob`.
- **UI:** Assistent (Quellserver/Login), Status/Fortschritt je Job.
- **Schreibrecht** nötig.

### 4.9 Mailbox-Passwort ändern (Priorität niedrig)
- **API:** `edit/mailbox` (Feld `password`/`password2`).
- **UI:** in den Konto-Einstellungen „Postfach-Passwort ändern".

### 4.10 Admin-Modus (Priorität niedrig, nur Domain-Admins)
- Domains, DKIM, TLS-Policy, Logs/Queue/Fail2ban.
- Eigenes, klar abgetrenntes „Admin"-Panel, nur bei Admin-Key sichtbar.

---

## 5. Sicherheit

- **API-Key** wie alle Secrets: nur im Main-Prozess, **safeStorage**-verschlüsselt, nie im Renderer.
- Alle Mailcow-Aufrufe laufen im Main-Prozess (`mailcowService.ts`), Renderer nur über IPC.
- **TLS-Validierung** standardmäßig an (nur self-signed bewusst erlauben, mit Warnung).
- Schreibaktionen mit **Bestätigungsdialog** (v. a. Löschen/Passwortänderung).
- Read-only-Key sauber behandeln: Schreib-UI ausgrauen statt Fehlerflut.
- **Fehlbedienungsschutz:** ein Admin-Key kann die ganze Mailcow steuern → deutlicher
  Hinweis beim Einrichten; empfohlen wird ein Domain-Admin-Key mit minimalem Scope.

---

## 6. Technische Umsetzung

- Neuer Main-Service **`src/main/services/mailcowService.ts`**:
  - schlanker Fetch-Wrapper (`X-API-Key`, Basis-URL, Timeout, Fehler-Mapping wie bei IMAP/SMTP).
  - Funktionen pro Block (listAppPasswords, addAlias, getQuarantine, …).
- **IPC** `mailcow:*` + Preload-Namespace `mailcow` (typisierte `IpcResult<T>`).
- **Typen** in `src/main/types` (MailcowConfig, AppPassword, Alias, QuarantineItem, …) als
  Single Source of Truth.
- **Speicherung** der Mailcow-Config pro Konto in `db.ts` (`StoredAccount.mailcow?` + `secret`).
- **UI** als eigener Einstellungen-Bereich „Mailcow" + ggf. eigene Ansichten (Quarantäne).

---

## 7. Phasenplan

1. **Phase 1 (Modus A):** Mailcow-Verbindung pro Konto (Key + Test) → App-Passwörter,
   Alias-Verwaltung, Wegwerf-Aliase, Quarantäne, Quota-Anzeige. Größter Nutzen, kein Backend.
2. **Phase 2:** Spam-WL/BL, Spam-Empfindlichkeit, Sync-Jobs, Passwort ändern.
3. **Phase 3:** Admin-Modus (Domain-Admins) — Domains/DKIM/TLS/Logs/Queue/Fail2ban.
4. **Phase 4 (optional):** Backend-Proxy (Modus B) für echte Endkunden-Szenarien.

---

## 8. Offene Fragen / Risiken

- **API-Stabilität:** Mailcow-Endpunkte/Felder können sich zwischen Versionen ändern →
  defensives Parsen, klare Fehlermeldungen, ggf. Versions-Check.
- **IP-Allowlist** ist die häufigste „Key geht nicht"-Ursache → prominenter Hilfetext.
- **Genaue Endpunktnamen** (Spam-Policy, Spam-Score) vor Implementierung gegen die
  konkrete Mailcow-Version verifizieren (API-Doku des Hosts / `/api/` Swagger).
- **Read-only-Erkennung:** Mailcow meldet den Key-Scope nicht immer eindeutig → ggf. über
  einen harmlosen Schreibtest oder Konfig-Schalter „Key ist read-only".
- **Abgrenzung zu vorhandenen Wegen:** Sieve bleibt über **ManageSieve** (nicht über die
  REST-API), um provider-neutral zu bleiben.
