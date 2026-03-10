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

#ifndef WebView2InstallerPath
  #error WebView2InstallerPath define required
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
OutputBaseFilename=WinGeminiSetup-{#Runtime}
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
Source: "{#WebView2InstallerPath}"; DestDir: "{tmp}"; DestName: "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Flags: deleteafterinstall

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\WinGeminiWrapper.exe"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\WinGeminiWrapper.exe"; Tasks: desktopicon

[Code]
function HasWebView2Runtime: Boolean;
var
  Version: string;
begin
  Result :=
    RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);
end;

[Run]
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/silent /install"; Flags: runhidden waituntilterminated; StatusMsg: "Installing Microsoft Edge WebView2 Runtime..."; Check: not HasWebView2Runtime
Filename: "{app}\WinGeminiWrapper.exe"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
