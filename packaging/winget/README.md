# winget-Veröffentlichung

N-MailClient kann über den Windows-Paketmanager installiert werden:

```powershell
winget install Lunasans.NMailClient
```

**Paket-ID:** `Lunasans.NMailClient` (dauerhaft, nicht ändern).

## Automatik (CI)

[`.github/workflows/winget.yml`](../../.github/workflows/winget.yml) reicht bei jedem
veröffentlichten Release automatisch ein aktualisiertes Manifest bei
`microsoft/winget-pkgs` ein (per `komac`/winget-releaser).

Dafür nötig: Repo-Secret **`WINGET_TOKEN`** = klassischer GitHub-PAT mit Scope
`public_repo` (forkt winget-pkgs und öffnet den PR).

## Einmalige Erst-Einreichung (Bootstrap)

Die Automatik aktualisiert nur ein **bestehendes** Paket. Die allererste Version muss
einmal manuell eingereicht werden — am einfachsten mit **komac**:

```powershell
# komac installieren (einmalig)
winget install komac

# interaktiv das neue Paket aus dem GitHub-Release erzeugen + PR einreichen
komac new Lunasans.NMailClient `
  --urls https://github.com/lunasans/N-MailClient/releases/download/v0.5.0/n-mailclient-Setup-0.5.0.exe `
  --version 0.5.0
```

komac lädt den Installer, berechnet den SHA256, erzeugt die drei Manifest-Dateien
(version / installer / locale) und öffnet den Pull Request. Nach der Freigabe durch
einen winget-Moderator übernimmt die CI alle weiteren Versionen automatisch.

## Hinweis zu Updates

Die App aktualisiert sich weiterhin **selbst** über electron-updater (GitHub-Releases).
winget ist nur ein zusätzlicher, sauberer **Installationsweg** ohne SmartScreen-Klick;
`winget upgrade` ist dadurch nur ergänzend.
