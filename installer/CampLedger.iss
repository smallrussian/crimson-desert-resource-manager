; Camp Ledger installer (Inno Setup 6). Build with installer\build.ps1, which publishes the app first.
; Per-user install, no admin prompt. Read-only toward the game; the optional "start with the game" task
; places only the launcher-only ASI, its ini and the ASI loader in the game's bin64 (AGENTS.md hard rule 1).

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{AE8EC4EE-E733-4593-AB1C-6F55DF8A7E55}
AppName=Camp Ledger
AppVersion={#AppVersion}
AppVerName=Camp Ledger {#AppVersion}
AppPublisher=Camp Ledger
DefaultDirName={localappdata}\Programs\Camp Ledger
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=out
OutputBaseFilename=CampLedgerSetup-{#AppVersion}
SetupIconFile=..\app\CampLedger.App\app.ico
UninstallDisplayIcon={app}\CampLedger.exe
UninstallDisplayName=Camp Ledger
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
; Ask to close a running Camp Ledger before replacing or removing its files.
AppMutex=CampLedger.App
CloseApplications=yes

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: startwithgame; Description: "&Start Camp Ledger automatically with Crimson Desert (adds a small launcher and ASI loader to the game's bin64 folder; removed again on uninstall)"; Flags: unchecked

[Files]
Source: "out\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Camp Ledger"; Filename: "{app}\CampLedger.exe"
Name: "{autodesktop}\Camp Ledger"; Filename: "{app}\CampLedger.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CampLedger.exe"; Parameters: "--install-launcher --quiet"; Tasks: startwithgame; StatusMsg: "Setting Camp Ledger to start with the game..."; Flags: waituntilterminated
Filename: "{app}\CampLedger.exe"; Description: "Open Camp Ledger"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Removes only the launcher files it placed (and the loader only if unchanged). Your snapshots in
; %LOCALAPPDATA%\CampLedger are kept.
Filename: "{app}\CampLedger.exe"; Parameters: "--remove-launcher --quiet"; RunOnceId: "RemoveLauncher"; Flags: waituntilterminated

[Code]
const
  WebView2Key = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function HasWebView2: Boolean;
var
  V: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V) and (V <> '') and (V <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM, WebView2Key, 'pv', V) and (V <> '') and (V <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, WebView2Key, 'pv', V) and (V <> '') and (V <> '0.0.0.0'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not HasWebView2 and not WizardSilent then
    MsgBox('Camp Ledger shows its window with the Microsoft Edge WebView2 Runtime, which was not found on this PC.' + #13#10#13#10 +
           'Install it from developer.microsoft.com/microsoft-edge/webview2, or use "Open in browser" from the Camp Ledger tray icon.',
           mbInformation, MB_OK);
end;
