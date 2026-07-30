<#
.SYNOPSIS
    Baut die eigenstaendige EXE und das Setup, beides signiert.

.DESCRIPTION
    Ein Durchlauf: veroeffentlichen -> Anwendung signieren -> Setup bauen ->
    Setup signieren -> Ergebnis pruefen.

    Signiert wird mit Set-AuthenticodeSignature. Das ist in Windows enthalten;
    signtool.exe aus dem Windows-SDK wird nicht gebraucht.

    Fuer den Wechsel auf ein echtes Zertifikat genuegt ein anderer Fingerabdruck
    per -Thumbprint. Am Rest aendert sich nichts.

.PARAMETER Thumbprint
    Fingerabdruck des Zertifikats im Speicher Cert:\CurrentUser\My.
    Vorgabe ist das selbstsignierte 'CN=N-MailClient'.

.PARAMETER TimestampServer
    Zeitstempeldienst. Ohne Zeitstempel wird die Signatur ungueltig, sobald das
    Zertifikat ablaeuft - mit Zeitstempel bleibt sie gueltig, weil belegt ist,
    dass zum Zeitpunkt der Signatur alles in Ordnung war.

.PARAMETER SkipSign
    Nur bauen, nicht signieren.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Thumbprint AABBCC...   # echtes Zertifikat
#>
[CmdletBinding()]
param(
    [string]$Thumbprint = "879E770F5DF5FA8E50B4392C08C7B2A9C94A8F51",
    [string]$TimestampServer = "http://timestamp.digicert.com",
    [switch]$SkipSign
)

$ErrorActionPreference = "Stop"

$installer = $PSScriptRoot

$root      = Split-Path $installer -Parent
$projekt   = Join-Path $root "NMailClient\NMailClient.csproj"
$publish   = Join-Path $root "publish"
$standalone= Join-Path $publish "standalone"
$appExe    = Join-Path $standalone "NMailClient.exe"
$iss       = Join-Path $installer "NMailClient.iss"

function Schritt($text) { Write-Host "`n=== $text" -ForegroundColor Cyan }

# ---- Version -----------------------------------------------------------------

# Einzige Quelle ist die Projektdatei. Das Setup bekommt die Version uebergeben;
# stuende sie dort noch einmal, liefen die beiden Angaben irgendwann auseinander
# - und niemand merkt es, bis das Setup eine falsche Zahl anzeigt.
$version = ([xml](Get-Content $projekt -Raw)).Project.PropertyGroup.Version |
           Where-Object { $_ } | Select-Object -First 1

if (-not $version) { throw "In $projekt steht kein <Version>." }

Schritt "Version $version (aus der Projektdatei)"

# ---- Zertifikat --------------------------------------------------------------

$cert = $null
if (-not $SkipSign) {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
            Where-Object { $_.Thumbprint -eq $Thumbprint } |
            Select-Object -First 1

    if (-not $cert) {
        throw "Kein Zertifikat mit Fingerabdruck $Thumbprint in Cert:\CurrentUser\My gefunden."
    }
    if (-not $cert.HasPrivateKey) {
        throw "Zum Zertifikat $Thumbprint fehlt der private Schluessel."
    }
    if ($cert.NotAfter -lt (Get-Date)) {
        throw "Das Zertifikat ist am $($cert.NotAfter.ToString('dd.MM.yyyy')) abgelaufen."
    }

    Schritt "Zertifikat"
    Write-Host "  $($cert.Subject)"
    Write-Host "  gueltig bis $($cert.NotAfter.ToString('dd.MM.yyyy'))"
    if ($cert.Subject -eq $cert.Issuer) {
        Write-Host "  ACHTUNG: selbstsigniert - beseitigt die SmartScreen-Warnung nicht." -ForegroundColor Yellow
    }
}

# ---- Veroeffentlichen --------------------------------------------------------

Schritt "Veroeffentlichen (eigenstaendig, eine Datei)"

Get-Process NMailClient -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $standalone -Recurse -Force -ErrorAction SilentlyContinue

& dotnet publish $projekt -c Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    --output $standalone --nologo -v q

if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen." }
if (-not (Test-Path $appExe)) { throw "$appExe wurde nicht erzeugt." }

Write-Host ("  {0:N1} MB" -f ((Get-Item $appExe).Length / 1MB))

# ---- Anwendung signieren -----------------------------------------------------

function Signiere($pfad) {
    $ergebnis = Set-AuthenticodeSignature -FilePath $pfad -Certificate $cert `
                    -TimestampServer $TimestampServer -HashAlgorithm SHA256

    if ($ergebnis.Status -ne "Valid" -and $ergebnis.Status -ne "UnknownError") {
        throw "Signieren von $(Split-Path $pfad -Leaf) fehlgeschlagen: $($ergebnis.StatusMessage)"
    }

    # Zeitstempel gesondert pruefen: ohne ihn waere die Signatur nur so lange
    # gueltig wie das Zertifikat.
    $pruef = Get-AuthenticodeSignature $pfad
    $zeit  = if ($pruef.TimeStamperCertificate) { "mit Zeitstempel" } else { "OHNE ZEITSTEMPEL" }
    Write-Host "  $(Split-Path $pfad -Leaf): $($pruef.Status) $zeit"
}

if (-not $SkipSign) {
    Schritt "Anwendung signieren"
    Signiere $appExe
}

# ---- Setup bauen -------------------------------------------------------------

Schritt "Setup bauen"

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) { throw "ISCC.exe nicht gefunden. Inno Setup 6 installieren." }

$ausgabe = & $iscc "/DAppVersion=$version" $iss 2>&1
if ($LASTEXITCODE -ne 0) {
    $ausgabe | Select-Object -Last 20 | ForEach-Object { Write-Host $_ }
    throw "Inno Setup fehlgeschlagen."
}

$setup = Get-ChildItem $publish -Filter "NMailClient-*-setup.exe" |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $setup) { throw "Setup wurde nicht erzeugt." }
Write-Host ("  {0}  {1:N1} MB" -f $setup.Name, ($setup.Length / 1MB))

# ---- Setup signieren ---------------------------------------------------------

if (-not $SkipSign) {
    Schritt "Setup signieren"
    Signiere $setup.FullName
}

# ---- Ergebnis ----------------------------------------------------------------

Schritt "Ergebnis"

foreach ($datei in @($appExe, $setup.FullName)) {
    $s = Get-AuthenticodeSignature $datei
    $name = Split-Path $datei -Leaf

    Write-Host ("  {0,-34} {1,7:N1} MB  {2}" -f $name, ((Get-Item $datei).Length/1MB), $s.Status)

    if ($s.SignerCertificate) {
        Write-Host "      Herausgeber: $($s.SignerCertificate.Subject)"
    }
}

Write-Host ""
Write-Host "  Hinweis: Bei einem selbstsignierten Zertifikat meldet Windows" -ForegroundColor Yellow
Write-Host "  'NotTrusted' bzw. 'UnknownError'. Das ist richtig so - vertraut" -ForegroundColor Yellow
Write-Host "  wird erst einem Zertifikat aus einer anerkannten Kette." -ForegroundColor Yellow
