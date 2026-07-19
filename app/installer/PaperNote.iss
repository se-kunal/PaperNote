; PaperNote installer (Inno Setup 6).
; Build: pass the published app folder and output dir as defines, e.g.
;   ISCC /DAppSrc="...\_temp\publish" /DOutDir="...\Publish" PaperNote.iss

#ifndef AppSrc
  #define AppSrc "..\..\_temp\publish"
#endif
#ifndef OutDir
  #define OutDir "..\..\Publish"
#endif
#define AppName "PaperNote"
#define AppVersion "1.0.0"
#define AppPublisher "PaperNote"
#define AppExe "PaperNote.exe"

[Setup]
AppId={{8F2C5E14-3B7A-4D9E-9C1F-PAPERNOTE001}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppVerName={#AppName} {#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayIcon={app}\{#AppExe}
OutputDir={#OutDir}
OutputBaseFilename=PaperNote-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
; Provide 1x/2x/3x so the modern wizard stays crisp on HiDPI displays.
WizardImageFile=banner.bmp,banner-2x.bmp,banner-3x.bmp
WizardSmallImageFile=small.bmp,small-2x.bmp,small-3x.bmp
SetupIconFile=..\PaperNote\Assets\app.ico
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#AppSrc}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
; "Open with PaperNote" for the text formats we import (.txt .md .markdown .log .json .csv).
; ProgId + per-extension OpenWithProgids; HKA follows the chosen install privileges.
Root: HKA; Subkey: "Software\Classes\PaperNote.Note"; ValueType: string; ValueData: "PaperNote Document"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\PaperNote.Note\DefaultIcon"; ValueType: string; ValueData: """{app}\{#AppExe}"",0"
Root: HKA; Subkey: "Software\Classes\PaperNote.Note\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\.txt\OpenWithProgids"; ValueType: string; ValueName: "PaperNote.Note"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "PaperNote.Note"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.markdown\OpenWithProgids"; ValueType: string; ValueName: "PaperNote.Note"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.log\OpenWithProgids"; ValueType: string; ValueName: "PaperNote.Note"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.json\OpenWithProgids"; ValueType: string; ValueName: "PaperNote.Note"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.csv\OpenWithProgids"; ValueType: string; ValueName: "PaperNote.Note"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"

[Run]
; Install the WebView2 runtime only if it isn't already here (silent, no-op if present).
; This is the one step that can take real time, so the message owns it honestly.
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; \
  Check: WebView2Missing; StatusMsg: "Preparing the editor engine (a one-time Windows component — not us getting heavier)..."; Flags: waituntilterminated
; No checkbox, no decision: the app simply opens when install finishes.
Filename: "{app}\{#AppExe}"; Flags: nowait skipifsilent

[Messages]
WelcomeLabel1=Let's get you writing.
WelcomeLabel2=PaperNote is a calm, fast home for everything you jot, plan, and think through.%n%n20 MB — installs in seconds%nNo account, no cloud, no lock-in%nYour notes are plain .md files on your disk%nUninstalls clean%n%nOne click and you're writing.
FinishedHeadingLabel=You're all set.
FinishedLabelNoIcons=PaperNote is already open and waiting. Press Ctrl+Alt+N anywhere in Windows for an instant note.
FinishedLabel=PaperNote is already open and waiting. Press Ctrl+Alt+N anywhere in Windows for an instant note.
ClickNext=Sounds good — let's go.
ConfirmUninstall=Remove %1 from this PC?%n%nYour notes stay safe in Documents\PaperNote — they're yours. Delete that folder yourself if you really want them gone.

[Code]
function GetTickCount: DWORD; external 'GetTickCount@kernel32.dll stdcall';

var
  InstallStart: DWORD;

function WebView2Missing: Boolean;
var
  v: string;
begin
  // Evergreen runtime registers its version under these keys (per-machine or per-user).
  Result := not (
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) or
    RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v)
  );
end;

// The install being fast IS the message: stamp the measured time on the finish page.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ElapsedMs: DWORD;
begin
  if CurStep = ssInstall then
    InstallStart := GetTickCount
  else if CurStep = ssPostInstall then
  begin
    ElapsedMs := GetTickCount - InstallStart;
    WizardForm.FinishedLabel.Caption :=
      'That''s it — ' + IntToStr(ElapsedMs div 1000) + '.' + IntToStr((ElapsedMs mod 1000) div 100)
      + ' seconds.' + #13#10#13#10
      + 'PaperNote is already open and waiting. Press Ctrl+Alt+N anywhere in Windows for an instant note.';
  end;
end;
