#define MyAppName "Win Gemini"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef Runtime
  #define Runtime "win-x64"
#endif

#ifndef SourceDir
  #error SourceDir define required
#endif

#ifndef OutputDir
  #error OutputDir define required
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "WinGeminiSetup-{#AppVersion}-{#Runtime}"
#endif

#ifndef DotNetRuntimeUrl
  #define DotNetRuntimeUrl "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
#endif

#ifndef RequiredDotNetRuntimePrefix
  #define RequiredDotNetRuntimePrefix "8.0"
#endif

#ifndef WebView2BootstrapperUrl
  #define WebView2BootstrapperUrl "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
#endif

#ifndef ProjectDir
  #define ProjectDir ".."
#endif

[Setup]
AppId={{C0D616B1-8A76-4F68-9364-2A8AB83AEF98}
AppName={#MyAppName}
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
SetupIconFile={#ProjectDir}\Assets\Icons\gemini.ico
UninstallDisplayIcon={app}\WinGeminiWrapper.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\WinGeminiWrapper.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\WinGeminiWrapper.exe"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\WinGeminiWrapper.exe"; Tasks: desktopicon

[Code]
var
  NeedsDotNetRuntime: Boolean;
  NeedsWebView2Runtime: Boolean;
  PrereqConsentAsked: Boolean;
  PrereqConsentGranted: Boolean;

function HasDotNetWindowsDesktopRuntime: Boolean;
var
  KeyPath: string;
  VersionKeys: TArrayOfString;
  I: Integer;
begin
  Result := False;
  KeyPath := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';

  if RegGetSubkeyNames(HKLM64, KeyPath, VersionKeys) then
  begin
    for I := 0 to GetArrayLength(VersionKeys) - 1 do
    begin
      if Pos('{#RequiredDotNetRuntimePrefix}.', VersionKeys[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;

  if RegGetSubkeyNames(HKLM, KeyPath, VersionKeys) then
  begin
    for I := 0 to GetArrayLength(VersionKeys) - 1 do
    begin
      if Pos('{#RequiredDotNetRuntimePrefix}.', VersionKeys[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function HasWebView2Runtime: Boolean;
var
  Version: string;
begin
  Result :=
    RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);
end;

function EnsurePrerequisiteConsent: Boolean;
var
  messageText: string;
begin
  if not (NeedsDotNetRuntime or NeedsWebView2Runtime) then
  begin
    Result := True;
    Exit;
  end;

  if PrereqConsentAsked then
  begin
    Result := PrereqConsentGranted;
    Exit;
  end;

  PrereqConsentAsked := True;

  messageText :=
    'This installation requires missing components and can download them now.' + #13#10 + #13#10 +
    '- .NET Windows Desktop Runtime {#RequiredDotNetRuntimePrefix}.x (if missing)' + #13#10 +
    '- Microsoft Edge WebView2 Runtime (if missing)' + #13#10 + #13#10 +
    'Do you want to download and install the missing components now?';

  PrereqConsentGranted := MsgBox(messageText, mbConfirmation, MB_YESNO) = IDYES;
  if not PrereqConsentGranted then
  begin
    MsgBox('Installation canceled because required components were declined.', mbError, MB_OK);
  end;

  Result := PrereqConsentGranted;
end;

function InitializeSetup(): Boolean;
begin
  NeedsDotNetRuntime := not HasDotNetWindowsDesktopRuntime;
  NeedsWebView2Runtime := not HasWebView2Runtime;
  PrereqConsentAsked := False;
  PrereqConsentGranted := False;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not EnsurePrerequisiteConsent() then
  begin
    Result := 'Installation canceled because required components were declined.';
  end;
end;

function ShouldInstallDotNetRuntime: Boolean;
begin
  Result := NeedsDotNetRuntime and EnsurePrerequisiteConsent();
end;

function ShouldInstallWebView2Runtime: Boolean;
begin
  Result := NeedsWebView2Runtime and EnsurePrerequisiteConsent();
end;

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$ErrorActionPreference='Stop'; $url='{#DotNetRuntimeUrl}'; $out=Join-Path $env:TEMP 'windowsdesktop-runtime-installer.exe'; Invoke-WebRequest -Uri $url -OutFile $out; Start-Process -FilePath $out -ArgumentList '/install /quiet /norestart' -Wait"""; Flags: runhidden waituntilterminated; StatusMsg: "Installing .NET Windows Desktop Runtime..."; Check: ShouldInstallDotNetRuntime
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$ErrorActionPreference='Stop'; $url='{#WebView2BootstrapperUrl}'; $out=Join-Path $env:TEMP 'MicrosoftEdgeWebView2Setup.exe'; Invoke-WebRequest -Uri $url -OutFile $out; Start-Process -FilePath $out -ArgumentList '/silent /install' -Wait"""; Flags: runhidden waituntilterminated; StatusMsg: "Installing Microsoft Edge WebView2 Runtime..."; Check: ShouldInstallWebView2Runtime
Filename: "{app}\WinGeminiWrapper.exe"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
