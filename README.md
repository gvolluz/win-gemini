# Win Gemini + NotebookLM Wrapper (Windows 11 Native)

A native Windows desktop wrapper for Gemini and NotebookLM using `WinForms + WebView2`.

## What it does

- Native Windows app (`.NET`, no Electron).
- Uses embedded Edge (`WebView2`) to render Gemini and NotebookLM exactly as in the browser.
- Auto-hidden top switcher to toggle between `Gemini` and `NotebookLM` (reveals on hover at top edge).
- Keeps one live `WebView2` instance per app, so switching does not force a full reload each time.
- Saves last visited URL for each app and restores it on next launch.
- Saves window size, position, and state (normal/maximized/minimized) as they change, and restores them on startup.
- Includes a settings page with close-button behavior: `Minimize to tray` or `Close app`.
- Startup sign-in gate:
  - If an existing Google session is present, the login window stays hidden and the app opens directly.
  - If not signed in, a login window appears and uses the normal Google sign-in flow.
- System tray support:
  - Close/minimize hides the app to tray.
  - Tray menu provides open/switch actions, `Refresh`, `Settings`, and `Exit`.
- Persistent session/profile data is stored under:
  - `%LOCALAPPDATA%\WinGeminiWrapper\WebView2`
- Local app state is stored at:
  - `%LOCALAPPDATA%\WinGeminiWrapper\app-state.json`

## Prerequisites

- Windows 11
- .NET 8 SDK
- WebView2 Runtime (usually already installed with Microsoft Edge)

## Build and run

```powershell
dotnet restore
dotnet run
```

## Build release EXE and installer

```powershell
.\build-installer.ps1
```

Outputs:

- App EXE (single-file): `artifacts\publish\win-x64\WinGeminiWrapper.exe`
- Installer EXE: `artifacts\installer\WinGeminiSetup-win-x64.exe`
- Bundled prerequisite payload: `artifacts\prereqs\MicrosoftEdgeWebView2RuntimeInstallerX64.exe`

The setup EXE includes the app and the offline WebView2 Runtime installer, and installs WebView2 silently if it is missing.

## Project files

- `Program.cs` - startup flow (login gate then main window)
- `LoginForm.cs` - hidden-first login/session checker
- `MainForm.cs` - wrapper window, app switcher, and tray integration
- `SettingsForm.cs` - app settings dialog
- `AppStateStore.cs` - persisted wrapper state (URL, window placement, settings)
- `NavigationClassifier.cs` - URL-based sign-in/session detection for Gemini + NotebookLM
- `AppConfig.cs` - shared URLs and persistent profile path

## TODO

- Add logout to wrapper.
