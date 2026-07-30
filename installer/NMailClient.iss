; Setup für N-MailClient (C#-PoC)
;
; Installation fuer alle Benutzer nach C:\Program Files. Das kostet eine
; Rechteabfrage, schuetzt die EXE aber vor Austausch durch etwas, das unter
; dem Benutzerkonto laeuft. Konten, Schluessel und Zwischenspeicher liegen
; weiterhin im jeweiligen Benutzerprofil.
;
; Bauen:  ..\installer\build.ps1   (nicht ISCC direkt - die Version fehlt dann)
; Erwartet die veröffentlichten Dateien unter ..\publish\standalone

#define AppName "N-MailClient"
#define AppPublisher "lunasans"

; Die Version kommt von build.ps1 (ISCC /DAppVersion=...) und stammt dort aus
; der Projektdatei. Sie hier fest einzutragen hiesse, sie an zwei Stellen zu
; pflegen — und genau so laufen solche Angaben auseinander.
#ifndef AppVersion
  #error Die Version fehlt. Bitte ueber build.ps1 bauen, nicht ISCC direkt aufrufen.
#endif

#define AppUrl "https://github.com/lunasans/N-MailClient"
#define AppExe "NMailClient.Poc.exe"
#define SourceDir "..\publish\standalone"

[Setup]
AppId={{7C1F4E2A-9B3D-4A6E-8F51-2D8A6B4C9E13}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases

; Installation für alle Benutzer nach C:\Program Files\N-MailClient.
;
; Das kostet eine Rechteabfrage bei Installation und Update, bringt aber den
; Schreibschutz des Ordners: die EXE lässt sich nicht durch etwas ersetzen, das
; unter dem Benutzerkonto läuft. Bei einem Programm, das Zugangsdaten verwaltet,
; ist das der Preis wert.
PrivilegesRequired=admin
DefaultDirName={commonpf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no

OutputDir=..\publish
OutputBaseFilename=NMailClient-{#AppVersion}-setup
SetupIconFile=..\NMailClient.Poc\Assets\appicon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}

; Die EXE ist bereits komprimiert (EnableCompressionInSingleFile); lzma2/max
; holt trotzdem noch etwas heraus.
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; 64-Bit-Anwendung: nicht auf 32-Bit-Windows anbieten.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Läuft die Anwendung noch, wird sie sauber beendet statt die Dateien zu sperren.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
de.DesktopIcon=Verknüpfung auf dem Desktop anlegen
de.KeepData=Konten, Einstellungen und Zwischenspeicher behalten
de.KeepDataInfo=Konten und Zwischenspeicher liegen im Benutzerprofil. Betroffen ist nur das gerade angemeldete Konto - Daten anderer Benutzer bleiben unangetastet.
de.RemoveData=Konten, Einstellungen und zwischengespeicherte Nachrichten löschen
de.RemoveDataNote=Passwörter im Anmeldeinformationsverwalter werden dabei nicht angetastet.

en.DesktopIcon=Create a desktop shortcut
en.KeepData=Keep accounts, settings and local cache
en.KeepDataInfo=Accounts and cache live in the user profile. Only the currently signed-in account is affected - other users' data is left alone.
en.RemoveData=Delete accounts, settings and cached messages
en.RemoveDataNote=Passwords in the Credential Manager are left untouched.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

; Kein Autostart-Haken hier: das Setup läuft mit Administratorrechten und würde
; den Eintrag in dessen Benutzerprofil schreiben, nicht in das des späteren
; Anwenders. Die Anwendung bietet den Schalter selbst an (Einstellungen →
; Allgemein → Mit Windows starten) und schreibt ihn dort, wo er hingehört.

[Files]
; Die eigenständige EXE bringt die .NET-Laufzeit mit — auf dem Zielrechner
; muss nichts vorinstalliert sein.
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Der WebView2-Benutzerdatenordner ist reiner Zwischenspeicher.
Type: filesandordirs; Name: "{localappdata}\NMailClient.Poc\WebView2"

[Code]

{ Beim Deinstallieren fragen, ob die Benutzerdaten mit sollen. Vorgabe ist
  behalten: wer neu installiert, will seine Konten nicht neu einrichten. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Antwort: Integer;
  Appdata: string;
  Localdata: string;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  Appdata := ExpandConstant('{userappdata}\NMailClient.Poc');
  Localdata := ExpandConstant('{localappdata}\NMailClient.Poc');

  if not (DirExists(Appdata) or DirExists(Localdata)) then
    Exit;

  Antwort := MsgBox(
    ExpandConstant('{cm:KeepDataInfo}') + #13#10#13#10 +
    ExpandConstant('{cm:RemoveData}') + '?' + #13#10#13#10 +
    ExpandConstant('{cm:RemoveDataNote}'),
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2);

  { Vorgabe ist Nein — Konten und Post gehen sonst wortlos verloren. }
  if Antwort = IDYES then
  begin
    DelTree(Appdata, True, True, True);
    DelTree(Localdata, True, True, True);
  end;
end;
