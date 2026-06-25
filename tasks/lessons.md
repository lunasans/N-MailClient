# Lessons

## Setup-Entscheidungen
- **better-sqlite3 vermieden**: natives Modul, müsste unter Windows gegen Electrons ABI
  rekompiliert werden (VS Build Tools). Für den MVP stattdessen JSON-Store im userData.
  Migration auf SQLite ist später isoliert in `src/main/services/db.ts` möglich.
- **Config-Dateien als CommonJS**: ohne `"type": "module"` in package.json müssen
  `postcss.config.js` / `tailwind.config.js` `module.exports` nutzen (kein `export default`).

- **`ELECTRON_RUN_AS_NODE=1` in dieser Umgebung gesetzt**: zwingt Electron in den reinen
  Node-Modus → `require('electron')` liefert den Binär-Pfad statt der API → `app` ist undefined.
  Zum Starten der GUI muss die Variable **komplett entfernt** werden (`env -u ELECTRON_RUN_AS_NODE`).
  Achtung: `ELECTRON_RUN_AS_NODE=` (leer aber gesetzt) genügt NICHT — Electron prüft nur Präsenz.

- **Keine Steuerzeichen-Bereiche (`\x00-\x1f`) in Regex-Literalen via Write-Tool**: führte
  reproduzierbar zu rohen NUL-Bytes in der `.ts`-Datei (binär, Edit-Tool findet Strings nicht mehr).
  Stattdessen Steuerzeichen programmatisch prüfen: `ch.charCodeAt(0) < 32`. Siehe
  `attachmentService.ts` → `sanitizeSegment`.

- **`webdav` v5 ist ESM-only** → bricht im CommonJS-Main-Bundle von Electron (Node 20, kein
  `require(ESM)`). Lösung: `webdav@^4` (CommonJS). Verifiziert mit
  `node -e "require('webdav').createClient"`.

- **Build-EPERM nicht vorschnell nur laufendem Dev-Prozess zuschreiben**: Auch nach beendetem
  Dev-Server kann `electron-vite build` beim Leeren von `out/main/index.js` mit EPERM scheitern.
  Erst Prozesse prüfen, dann Datei-/Cloud-Sync-Lock oder Rechte auf `out/` separat untersuchen.

(Weitere Lessons nach Korrekturen ergänzen.)
